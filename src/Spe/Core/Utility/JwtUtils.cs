﻿using Sitecore.Exceptions;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Sitecore.Diagnostics;
using Sitecore.layouts.testing;
using Spe.Core.Diagnostics;
using Spe.Core.Extensions;
using Spe.Core.Settings.Authorization;

namespace Spe.Core.Utility
{
    public static class JwtUtils
    {
        public class TokenHeader
        {
            public string Alg { get; set; }
            public string Typ { get; set; }
        }

        public class TokenPayload
        {
            public string Iss { get; set; }
            public string Aud { get; set; }
            public long Exp { get; set; }
            public string Name { get; set; }
        }

        private static bool IsValidSharedSecret(out string secret)
        {
            var authProvider = SpeConfigurationManager.AuthenticationProvider;
            secret = authProvider.SharedSecret;

            SecurityException error = null;
            var isValid = true;
            if (string.IsNullOrWhiteSpace(secret))
                error = new SecurityException("The SPE shared secret is not set. Add a child <SharedSecret> element in the SPE <authenticationProvider> config (Spe.config) and set a secure shared secret, e.g. a 64-char random string.");

            if (double.TryParse(secret, out _))
                error = new SecurityException("The SPE shared secret is not set, or was set to a numeric value. Add a child <SharedSecret> element in the SPE <authenticationProvider> config (Spe.config) and set a secure shared secret, e.g. a 64-char random string.");

            if (secret.Length < 30)
                error = new SecurityException("Your SPE shared secret is not long enough. Please make it more than 30 characters for maximum security. You can set this in Spe.config on the <authenticationProvider>.");

            if (error != null)
            {
                isValid = false;
            }

            if (authProvider.DetailedAuthenticationErrors && error != null)
            {
                throw error;
            }

            if (isValid) return true;

            return false;
        }

        private static bool IsValidTokenType(string type)
        {
            var isValid = !string.IsNullOrEmpty(type) && type.Is("JWT");
            if (isValid) return true;
            
            var authProvider = SpeConfigurationManager.AuthenticationProvider;
            if(authProvider.DetailedAuthenticationErrors)
                throw new SecurityException("The Token Type is incorrect.");

            return false;
        }

        private static bool IsValidAudience(string authority, string audience)
        {
            var authProvider = SpeConfigurationManager.AuthenticationProvider;
            var isValid = !string.IsNullOrEmpty(audience) && 
                          (audience.Is(authority) || 
                           authProvider.AllowedAudiences.Any() && 
                           authProvider.AllowedAudiences.Contains(audience));
            if (isValid) return true;

            if(authProvider.DetailedAuthenticationErrors)
                throw new SecurityException("The Token Audience is not allowed.");

            return false;
        }

        private static bool IsValidIssuer(string issuer)
        {
            var authProvider = SpeConfigurationManager.AuthenticationProvider;
            var isValid = !string.IsNullOrEmpty(issuer) &&
                          authProvider.AllowedIssuers.Any() &&
                          authProvider.AllowedIssuers.Contains(issuer);
            if (isValid) return true;
            
            if(authProvider.DetailedAuthenticationErrors)
                throw new SecurityException("The Token Issuer is not allowed.");

            return false;
        }

        private static bool IsValidExpiration(long expiration)
        {
            var nowUtc = DateTime.UtcNow;
            var expireUtc = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expiration);
            var isValid = nowUtc < expireUtc;
            if (isValid) return true;
            
            var authProvider = SpeConfigurationManager.AuthenticationProvider;
            if(authProvider.DetailedAuthenticationErrors)
                throw new SecurityException("The Token Expiration has passed.");

            return false;
        }

        private static bool IsValidSignature(string providedSignature, string testSignature)
        {
            var isValid = providedSignature == testSignature;
            if (isValid) return true;
            
            var authProvider = SpeConfigurationManager.AuthenticationProvider;
            if(authProvider.DetailedAuthenticationErrors)
                throw new SecurityException("The Token signatures do not match.");

            return false;
        }

        private static bool IsValidUsername(string name)
        {
            var isValid = !string.IsNullOrEmpty(name);
            if (isValid) return true;

            var authProvider = SpeConfigurationManager.AuthenticationProvider;
            if(authProvider.DetailedAuthenticationErrors)
                throw new SecurityException("The name provided must be a valid username.");

            return false;
        }

        private static byte[] Decode(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException(nameof(input));

            var output = input;
            output = output.Replace('-', '+'); // 62nd char of encoding
            output = output.Replace('_', '/'); // 63rd char of encoding
            switch (output.Length % 4) // Pad with trailing '='s
            {
                case 0:
                    break; // No pad chars in this case
                case 2:
                    output += "==";
                    break; // Two pad chars
                case 3:
                    output += "=";
                    break; // One pad char
                default:
                    throw new FormatException("Illegal base64url string.");
            }
            var converted = Convert.FromBase64String(output); // Standard base64 decoder
            return converted;
        }

        private static byte[] ComputeHash(string algorithm, string secret, string toBeSigned)
        {
            var secretBytes = Encoding.UTF8.GetBytes(secret);
            switch (algorithm)
            {
                case "HS256":
                    return new HMACSHA256(secretBytes).ComputeHash(Encoding.UTF8.GetBytes(toBeSigned));
                case "HS384":
                    return new HMACSHA384(secretBytes).ComputeHash(Encoding.UTF8.GetBytes(toBeSigned));
                case "HS512":
                    return new HMACSHA512(secretBytes).ComputeHash(Encoding.UTF8.GetBytes(toBeSigned));
                default:
                    return null;
            }
        }

        public static bool ValidateToken(string token, string authority, out string username)
        {
            username = null;
            if (string.IsNullOrEmpty(token)) return false;
            if (!IsValidSharedSecret(out var secret)) return false;

            var parts = token.Split(new[] { "." }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return false;

            var serializer = new JavaScriptSerializer();

            var headerJsonBase64 = parts[0];
            var decodedHeader = Decode(headerJsonBase64);
            var header = Encoding.UTF8.GetString(decodedHeader);
            var tokenHeader = serializer.Deserialize<TokenHeader>(header);

            if (!IsValidTokenType(tokenHeader.Typ)) return false;

            var payloadJsonBase64 = parts[1];
            var decodedPayload = Decode(payloadJsonBase64);
            var payload = Encoding.UTF8.GetString(decodedPayload);
            var tokenPayload = serializer.Deserialize<TokenPayload>(payload);

            if (!IsValidExpiration(tokenPayload.Exp)) return false;
            if (!IsValidAudience(tokenPayload.Aud, authority)) return false;
            if (!IsValidIssuer(tokenPayload.Iss)) return false;

            var signature = parts[2];

            var toBeSigned = $"{headerJsonBase64}.{payloadJsonBase64}";

            var hash = ComputeHash(tokenHeader.Alg, secret, toBeSigned);
            var testSignature = Convert.ToBase64String(hash).Split('=')[0]
                .Replace('+', '-').Replace('/', '_');

            if (!IsValidSignature(signature, testSignature)) return false;
            if (!IsValidUsername(tokenPayload.Name)) return false;
            username = tokenPayload.Name;
            return true;
        }
    }
}