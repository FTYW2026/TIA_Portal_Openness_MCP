using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcpServer
{
    public static class CommercialLicense
    {
        private const string PublicKeyXml = "<RSAKeyValue><Modulus>v9bPKUR7vMAM5L/C5tjZUW2uYPAj9l9LEqgJKNIiwwSu2EUkjPa9i8wkbpJ7hBhwtOn0hJ8dAJu3T1n1mXbOaHUgVlYQJyk0ivFqJtUENpr620rhTCFMN2ReS+Is7PA4lfXZ2/u9kemjH6BXGMxvHKRgm9QtJtENR3uaFLSA5dtOUodpdk0ws4N1bUxedLgaIVTn9iXc7f6CWpp9o0pvLNEfhwwOz2YPAVBSEE0k9nX7lK7NYIzGbKK+4M4bII/O22SFV/2+sMGxtk+0mmGWjL9WGAuo47GtNpMJI9+T+JAmX0ieQQoYfAu9CtuiTCTgFGmwsDlPajdajnJ2k7iJyQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        private const string LicenseFileName = "TiaMcpServer.license.json";
        private const string CommercialLockFileName = "commercial.lock";

        public static bool IsCommercialLocked()
        {
            return File.Exists(Path.Combine(AppContext.BaseDirectory, CommercialLockFileName));
        }

        public static string GetMachineCode()
        {
            var raw = string.Join("|", new[]
            {
                Environment.MachineName,
                ReadWmi("Win32_ComputerSystemProduct", "UUID"),
                ReadWmi("Win32_BIOS", "SerialNumber"),
                ReadWmi("Win32_BaseBoard", "SerialNumber")
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return BitConverter.ToString(hash).Replace("-", "");
        }

        public static void PrintMachineCode(TextWriter writer)
        {
            writer.WriteLine("TIA MCP commercial machine code:");
            writer.WriteLine(GetMachineCode());
            writer.WriteLine();
            writer.WriteLine("Send this machine code to the package owner to request TiaMcpServer.license.json.");
        }

        public static void EnsureValidForCommercialRun()
        {
            if (!IsCommercialLocked()) return;

            var result = ValidateLicense();
            if (result.Ok) return;

            throw new UnauthorizedAccessException(
                "TIA MCP commercial package is locked and no valid license was found. " +
                result.Message + " Run TiaMcpServer.exe --license-machine-code and request a signed license from the package owner.");
        }

        public static (bool Ok, string Message) ValidateLicense()
        {
            var path = Path.Combine(AppContext.BaseDirectory, LicenseFileName);
            if (!File.Exists(path))
            {
                return (false, $"Missing {LicenseFileName}.");
            }

            try
            {
                var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (json == null) return (false, "License file is not a JSON object.");

                var payloadNode = json["payload"] as JsonObject;
                var signature = json["signature"]?.ToString() ?? "";
                if (payloadNode == null || string.IsNullOrWhiteSpace(signature))
                {
                    return (false, "License must contain payload and signature.");
                }

                var payload = CanonicalPayload(payloadNode);
                var machineCode = payloadNode["machineCode"]?.ToString() ?? "";
                var expiresOnText = payloadNode["expiresOn"]?.ToString() ?? "";
                var product = payloadNode["product"]?.ToString() ?? "";
                if (!string.Equals(product, "TIA_MCP_COMMERCIAL", StringComparison.Ordinal))
                {
                    return (false, "License product does not match.");
                }
                if (!string.Equals(machineCode, GetMachineCode(), StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "License machine code does not match this computer.");
                }
                if (!DateTime.TryParse(expiresOnText, out var expiresOn))
                {
                    return (false, "License expiresOn is invalid.");
                }
                if (DateTime.UtcNow.Date > expiresOn.ToUniversalTime().Date)
                {
                    return (false, "License has expired.");
                }

                using var rsa = RSA.Create();
                rsa.FromXmlString(PublicKeyXml);
                var ok = rsa.VerifyData(
                    Encoding.UTF8.GetBytes(payload),
                    Convert.FromBase64String(signature),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                return ok ? (true, "License OK.") : (false, "License signature is invalid.");
            }
            catch (Exception ex)
            {
                return (false, "License validation failed: " + ex.Message);
            }
        }

        public static string CanonicalPayload(JsonObject payload)
        {
            var customer = payload["customer"]?.ToString() ?? "";
            var expiresOn = payload["expiresOn"]?.ToString() ?? "";
            var issuedAt = payload["issuedAt"]?.ToString() ?? "";
            var machineCode = payload["machineCode"]?.ToString() ?? "";
            var product = payload["product"]?.ToString() ?? "";
            return "customer=" + customer + "\n" +
                   "expiresOn=" + expiresOn + "\n" +
                   "issuedAt=" + issuedAt + "\n" +
                   "machineCode=" + machineCode + "\n" +
                   "product=" + product + "\n";
        }

        private static string ReadWmi(string className, string propertyName)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT " + propertyName + " FROM " + className);
                foreach (ManagementObject item in searcher.Get())
                {
                    return item[propertyName]?.ToString()?.Trim() ?? "";
                }
            }
            catch
            {
                return "";
            }

            return "";
        }
    }
}
