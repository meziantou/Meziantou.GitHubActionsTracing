using System.Security.Cryptography;
using System.Text;

namespace Meziantou.GitHubActionsTracing.Server;

internal static class GitHubWebhookSignatureValidator
{
    private const string SignaturePrefix = "sha256=";

    public static bool IsValid(ReadOnlySpan<byte> payload, IHeaderDictionary headers, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return true;
        }

        if (!headers.TryGetValue("X-Hub-Signature-256", out var signatureHeaderValues))
        {
            return false;
        }

        var signatureHeader = signatureHeaderValues.ToString();
        if (!signatureHeader.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var expectedSignature = Convert.FromHexString(signatureHeader[SignaturePrefix.Length..]);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var computedSignature = hmac.ComputeHash(payload.ToArray());
            return CryptographicOperations.FixedTimeEquals(computedSignature, expectedSignature);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
