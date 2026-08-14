using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AssetBeeDrone.Collectors.Linux;

/// <summary>
/// Minimal AWS Signature Version 4 helper for EC2 Query API GET requests (AOT-safe).
/// </summary>
internal static class AwsSigV4
{
    public static string BuildAuthorizationHeader(
        string host,
        string canonicalQueryString,
        string region,
        string service,
        string accessKeyId,
        string secretAccessKey,
        string? sessionToken,
        DateTimeOffset utcNow,
        out string amzDate)
    {
        amzDate = utcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string dateStamp = utcNow.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";

        List<string> signedHeadersList = ["host", "x-amz-date"];
        if (!string.IsNullOrEmpty(sessionToken))
        {
            signedHeadersList.Add("x-amz-security-token");
        }

        signedHeadersList.Sort(StringComparer.Ordinal);
        string signedHeaders = string.Join(';', signedHeadersList);

        StringBuilder canonicalHeadersBuilder = new();
        foreach (string name in signedHeadersList)
        {
            string value = name switch
            {
                "host" => host,
                "x-amz-date" => amzDate,
                "x-amz-security-token" => sessionToken!,
                _ => string.Empty
            };
            canonicalHeadersBuilder.Append(name).Append(':').Append(value.Trim()).Append('\n');
        }

        string canonicalRequest = string.Join('\n',
            "GET",
            "/",
            canonicalQueryString,
            canonicalHeadersBuilder.ToString(),
            signedHeaders,
            HashHex(Encoding.UTF8.GetBytes(string.Empty)));

        string stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            HashHex(Encoding.UTF8.GetBytes(canonicalRequest)));

        byte[] signingKey = GetSignatureKey(secretAccessKey, dateStamp, region, service);
        string signature = ToHex(HmacSha256(signingKey, stringToSign));

        return
            $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{credentialScope}, " +
            $"SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static byte[] GetSignatureKey(
        string secretAccessKey, string dateStamp, string region, string service)
    {
        byte[] kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        byte[] kRegion = HmacSha256(kDate, region);
        byte[] kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, "aws4_request");
    }

    private static byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string HashHex(byte[] data) => ToHex(SHA256.HashData(data));

    private static string ToHex(byte[] bytes)
    {
        StringBuilder builder = new(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
