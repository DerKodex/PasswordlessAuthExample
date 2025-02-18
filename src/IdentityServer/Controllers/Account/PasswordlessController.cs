﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fido2NetLib;
using Fido2NetLib.Development;
using Fido2NetLib.Objects;
using IdentityServer4;
using IdentityServer4.Events;
using IdentityServer4.Extensions;
using IdentityServer4.Services;
using IdentityServer4.Test;
using IdentityServerHost.Quickstart.UI;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IdentityServer.Controllers.Account
{
    public class PasswordlessController : Controller
    {
        private IFido2 _fido2;
        public static readonly DevelopmentInMemoryStore PasswordlessStore = new DevelopmentInMemoryStore();
        private readonly TestUserStore _users;
        private readonly IEventService _events;
        
        public PasswordlessController(IFido2 fido2, TestUserStore users, IEventService events, IIdentityServerInteractionService interaction)
        {
            _fido2 = fido2;
            _events = events;
            _users = users ?? new TestUserStore(TestUsers.Users);;
        }

        private string FormatException(Exception e)
        {
            return string.Format("{0}{1}", e.Message, e.InnerException != null ? " (" + e.InnerException.Message + ")" : "");
        }

        [HttpPost]
        [Route("/makeCredentialOptions")]
        public JsonResult MakeCredentialOptions([FromForm] string username,
                                                [FromForm] string displayName,
                                                [FromForm] string attType,
                                                [FromForm] string authType,
                                                [FromForm] bool requireResidentKey,
                                                [FromForm] string userVerification)
        {
            try
            {
                // user must already exist in Identity
                var identityUser = _users.FindByUsername(username);
                if (identityUser == null) {
                    throw new Exception("User not found");
                }

                if (!HttpContext.User.IsAuthenticated())
                {
                    throw new Exception("User is not authenticated");
                };

                // 1. Get user from DB by username (in our example, auto create missing users)
                var user = PasswordlessStore.GetOrAddUser(username, () => new Fido2User
                {
                    DisplayName = displayName,
                    Name = username,
                    Id = Encoding.UTF8.GetBytes(username) // byte representation of userID is required
                });

                // 2. Get user existing keys by username
                var existingKeys = PasswordlessStore.GetCredentialsByUser(user).Select(c => c.Descriptor).ToList();

                // 3. Create options
                var authenticatorSelection = new AuthenticatorSelection
                {
                    RequireResidentKey = requireResidentKey,
                    UserVerification = userVerification.ToEnum<UserVerificationRequirement>()
                };

                if (!string.IsNullOrEmpty(authType))
                    authenticatorSelection.AuthenticatorAttachment = authType.ToEnum<AuthenticatorAttachment>();

                var exts = new AuthenticationExtensionsClientInputs() 
                { 
                    Extensions = true, 
                    UserVerificationIndex = true, 
                    Location = true, 
                    UserVerificationMethod = true, 
                    BiometricAuthenticatorPerformanceBounds = new AuthenticatorBiometricPerfBounds 
                    { 
                        FAR = float.MaxValue, 
                        FRR = float.MaxValue 
                    } 
                };

                var options = _fido2.RequestNewCredential(user, existingKeys, authenticatorSelection, attType.ToEnum<AttestationConveyancePreference>(), exts);

                // 4. Temporarily store options, session/in-memory cache/redis/db
                HttpContext.Session.SetString("fido2.attestationOptions", options.ToJson());

                // 5. return options to client
                return Json(options);
            }
            catch (Exception e)
            {
                return Json(new CredentialCreateOptions { Status = "error", ErrorMessage = FormatException(e) });
            }
        }

        [HttpPost]
        [Route("/makeCredential")]
        public async Task<JsonResult> MakeCredential([FromBody] AuthenticatorAttestationRawResponse attestationResponse)
        {
            try
            {
                // 1. get the options we sent the client
                var jsonOptions = HttpContext.Session.GetString("fido2.attestationOptions");
                var options = CredentialCreateOptions.FromJson(jsonOptions);

                // 2. Create callback so that lib can verify credential id is unique to this user
                IsCredentialIdUniqueToUserAsyncDelegate callback = async (IsCredentialIdUniqueToUserParams args) =>
                {
                    var users = await PasswordlessStore.GetUsersByCredentialIdAsync(args.CredentialId);
                    if (users.Count > 0)
                        return false;

                    return true;
                };

                // 2. Verify and make the credentials
                var success = await _fido2.MakeNewCredentialAsync(attestationResponse, options, callback);

                // 3. Store the credentials in db
                PasswordlessStore.AddCredentialToUser(options.User, new StoredCredential
                {
                    Descriptor = new PublicKeyCredentialDescriptor(success.Result.CredentialId),
                    PublicKey = success.Result.PublicKey,
                    UserHandle = success.Result.User.Id,
                    SignatureCounter = success.Result.Counter,
                    CredType = success.Result.CredType,
                    RegDate = DateTime.Now,
                    AaGuid = success.Result.Aaguid
                });

                // 4. return "ok" to the client
                return Json(success);
            }
            catch (Exception e)
            {
                return Json(new Fido2.CredentialMakeResult { Status = "error", ErrorMessage = FormatException(e) });
            }
        }

        [HttpPost]
        [Route("/assertionOptions")]
        public ActionResult AssertionOptionsPost([FromForm] string username, [FromForm] string userVerification)
        {
            try
            {
                var existingCredentials = new List<PublicKeyCredentialDescriptor>();

                if (!string.IsNullOrEmpty(username))
                {
                    // 1. Get user from DB
                    var user = PasswordlessStore.GetUser(username);
                    if (user == null)
                        throw new ArgumentException("Username was not registered");

                    // 2. Get registered credentials from database
                    existingCredentials = PasswordlessStore.GetCredentialsByUser(user).Select(c => c.Descriptor).ToList();
                }

                var exts = new AuthenticationExtensionsClientInputs()
                { 
                    SimpleTransactionAuthorization = "FIDO", 
                    GenericTransactionAuthorization = new TxAuthGenericArg 
                    { 
                        ContentType = "text/plain", 
                        Content = new byte[] { 0x46, 0x49, 0x44, 0x4F } 
                    }, 
                    UserVerificationIndex = true, 
                    Location = true, 
                    UserVerificationMethod = true 
                };

                // 3. Create options
                var uv = string.IsNullOrEmpty(userVerification) ? UserVerificationRequirement.Discouraged : userVerification.ToEnum<UserVerificationRequirement>();
                var options = _fido2.GetAssertionOptions(
                    existingCredentials,
                    uv,
                    exts
                );

                // 4. Temporarily store options, session/in-memory cache/redis/db
                HttpContext.Session.SetString("fido2.assertionOptions", options.ToJson());

                // 5. Return options to client
                return Json(options);
            }

            catch (Exception e)
            {
                return Json(new AssertionOptions { Status = "error", ErrorMessage = FormatException(e) });
            }
        }

        [HttpPost]
        [Route("/makeAssertion")]
        public async Task<JsonResult> MakeAssertion([FromBody] AuthenticatorAssertionRawResponse clientResponse)
        {
            try
            {
                // 1. Get the assertion options we sent the client
                var jsonOptions = HttpContext.Session.GetString("fido2.assertionOptions");
                var options = AssertionOptions.FromJson(jsonOptions);

                // 2. Get registered credential from database
                var creds = PasswordlessStore.GetCredentialById(clientResponse.Id);

                if (creds == null)
                {
                    throw new Exception("Unknown credentials");
                }

                // 3. Get credential counter from database
                var storedCounter = creds.SignatureCounter;

                // 4. Create callback to check if userhandle owns the credentialId
                IsUserHandleOwnerOfCredentialIdAsync callback = async (args) =>
                {
                    var storedCreds = await PasswordlessStore.GetCredentialsByUserHandleAsync(args.UserHandle);
                    return storedCreds.Exists(c => c.Descriptor.Id.SequenceEqual(args.CredentialId));
                };

                // 5. Make the assertion
                var res = await _fido2.MakeAssertionAsync(clientResponse, options, creds.PublicKey, storedCounter,
                    callback);

                // 6. Store the updated counter
                PasswordlessStore.UpdateCounter(res.CredentialId, res.Counter);

                if (res.Status == "ok")
                {
                    var username = System.Text.Encoding.UTF8.GetString(creds.UserId);
                    await SignInOidc(username);
                }
                // 7. return OK to client
                return Json(res);
            }
            catch (Exception e)
            {
                return Json(new AssertionVerificationResult {Status = "error", ErrorMessage = FormatException(e)});
            }
        }

        async Task SignInOidc(string username)
        {
            var user = _users.FindByUsername(username);
            await _events.RaiseAsync(new UserLoginSuccessEvent(user.Username, user.SubjectId, user.Username));

            AuthenticationProperties props = new AuthenticationProperties();
            // issue authentication cookie with subject ID and username
            var isUser = new IdentityServerUser(user.SubjectId)
            {
                DisplayName = user.Username
            };

            await AuthenticationManagerExtensions.SignInAsync(HttpContext, isUser, props);
        }
    }
}