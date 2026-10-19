using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Spike #75: a minimal Web Push sender with in-box crypto only. Encrypts with <c>aes128gcm</c> (RFC 8291, which Apple's
/// push service requires) and signs with VAPID (RFC 8292). The real one would live in Infrastructure behind an
/// interface; this is the proof that it's small and works.
/// </summary>
public sealed class WebPushSender(HttpClient http, string vapidPublicKey, string vapidPrivateKey, string subject)
{
    public const int RecordSize = 4096;

    /// <summary>Push service answers, as the feature would act on them.</summary>
    public enum Outcome { Delivered, Gone, Retry, Refused }

    public async Task<(Outcome Outcome, int Status, string Reason)> SendAsync(
        string endpoint, string p256dh, string auth, string payload, int ttlSeconds, string? topic, string urgency,
        CancellationToken cancellationToken)
    {
        var body = Encrypt(Encoding.UTF8.GetBytes(payload), B64.Decode(p256dh), B64.Decode(auth));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.Authorization = new AuthenticationHeaderValue("vapid", $"t={VapidJwt(new Uri(endpoint))}, k={vapidPublicKey}");
        request.Headers.Add("TTL", ttlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("Urgency", urgency); // very-low | low | normal | high
        if (topic is not null)
        {
            request.Headers.Add("Topic", topic); // replaces an undelivered push with the same topic (≤ 32 URL-safe chars)
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        // Push services explain refusals in the body (Apple: {"reason":"BadJwtToken"}); it holds no secrets.
        var reason = response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.StatusCode switch
        {
            HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Accepted => Outcome.Delivered,
            HttpStatusCode.NotFound or HttpStatusCode.Gone => Outcome.Gone, // delete the subscription
            HttpStatusCode.TooManyRequests => Outcome.Retry,
            _ when status >= 500 => Outcome.Retry,
            _ => Outcome.Refused, // 400 bad request, 403 VAPID mismatch, 413 too big: a bug or configuration
        }, status, reason);
    }

    /// <summary>RFC 8291 encryption: one aes128gcm record (payloads are far below 4 KB).</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman? asKey = null, byte[]? salt = null)
    {
        using var ephemeral = asKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        salt ??= RandomNumberGenerator.GetBytes(16);
        var asPublic = RawPublicKey(ephemeral.ExportParameters(false));
        using var ua = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var ecdhSecret = ephemeral.DeriveRawSecretAgreement(ua.PublicKey);

        // IKM = HKDF(salt=auth_secret, ikm=ecdh_secret, info="WebPush: info\0" || ua_public || as_public, 32)
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var padded = Concat(plaintext, [0x02]); // the last (only) record's delimiter, no extra padding
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
        {
            aes.Encrypt(nonce, padded, cipher, tag);
        }

        // Header: salt (16) | rs (uint32 BE) | idlen (1) | keyid = as_public (65)
        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return Concat(header, cipher, tag);
    }

    /// <summary>RFC 8292: an ES256 JWT for the push service's origin, valid 12 hours (the maximum is 24).</summary>
    private string VapidJwt(Uri endpoint)
    {
        var header = B64.Encode(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        var claims = B64.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = endpoint.GetLeftPart(UriPartial.Authority),
            exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(),
            sub = subject,
        }));
        var publicKey = B64.Decode(vapidPublicKey);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B64.Decode(vapidPrivateKey),
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256); // r || s
        return $"{header}.{claims}.{B64.Encode(signature)}";
    }

    private static byte[] RawPublicKey(ECParameters p) => Concat([0x04], p.Q.X!, p.Q.Y!);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}

public static class B64
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}
