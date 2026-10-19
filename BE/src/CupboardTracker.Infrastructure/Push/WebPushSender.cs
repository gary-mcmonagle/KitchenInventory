using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Push;
using CupboardTracker.Domain.Push;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.Push;

/// <summary>
/// From app settings (#77): VAPID_PUBLIC_KEY (a Bicep parameter), VAPID_PRIVATE_KEY (a Key Vault reference to
/// <c>vapid-private-key</c>; until the secret exists the setting holds the reference text, which counts as not
/// configured) and PUSH_APP_ORIGIN (default https://kitchen.garymcmonagle.com): the VAPID subject Apple requires to be
/// real, and the base of every tap URL.
/// </summary>
public sealed record WebPushOptions(string? PublicKey, string? PrivateKey, Uri AppOrigin)
{
    public static readonly Uri DefaultAppOrigin = new("https://kitchen.garymcmonagle.com");

    public int Attempts { get; init; } = 3;

    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public bool IsConfigured => !string.IsNullOrEmpty(PublicKey) && !string.IsNullOrEmpty(PrivateKey);

    public static WebPushOptions FromConfiguration(IConfiguration config)
    {
        static string? Value(string? text) =>
            string.IsNullOrWhiteSpace(text) || text.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase) ? null : text.Trim();

        var origin = Uri.TryCreate(config["PUSH_APP_ORIGIN"], UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? new Uri(uri.GetLeftPart(UriPartial.Authority))
            : DefaultAppOrigin;
        return new WebPushOptions(Value(config["VAPID_PUBLIC_KEY"]), Value(config["VAPID_PRIVATE_KEY"]), origin);
    }
}

/// <summary>
/// Our own Web Push sender (#77, spike #75 option A): <c>aes128gcm</c> encryption (RFC 8291) and VAPID (RFC 8292) with
/// in-box crypto, which Apple's push service needs (the old <c>aesgcm</c> isn't accepted). It follows Apple's stricter
/// rules: a real VAPID subject, and a letters-and-digits <c>Topic</c>. The payload is Declarative Web Push JSON
/// (iOS 18.4+ shows it without the service worker; our worker shows it everywhere else). Throttling and server errors
/// are retried. Logs carry the push service's host and status only: never the endpoint (a capability URL), keys or
/// payload.
/// </summary>
public sealed partial class WebPushSender(
    HttpClient http, WebPushOptions options, TimeProvider clock, ILogger<WebPushSender> logger) : IPushSender
{
    public const int RecordSize = 4096;

    public async Task<PushOutcome> SendAsync(PushSubscription subscription, PushMessage message, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return PushOutcome.NotConfigured;
        }

        if (!PushServices.IsAllowed(subscription.Endpoint) || !PushTopic.IsValid(message.Topic))
        {
            return PushOutcome.Refused;
        }

        var endpoint = new Uri(subscription.Endpoint);
        var payload = Encoding.UTF8.GetBytes(Payload(message, options.AppOrigin));
        var delay = options.FirstRetryDelay;
        var status = 0;
        for (var attempt = 1; attempt <= options.Attempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);
                using var request = Request(endpoint, Encrypt(payload, B64.Decode(subscription.P256dh), B64.Decode(subscription.Auth)), message);
                using var response = await http.SendAsync(request, timeout.Token);
                status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return PushOutcome.Delivered;
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    LogGone(endpoint.Host, status);
                    return PushOutcome.Gone;
                }

                if (response.StatusCode != HttpStatusCode.TooManyRequests && status < 500)
                {
                    // Push services explain refusals in the body (Apple: {"reason":"BadJwtToken"}); it holds no secrets.
                    var reason = await response.Content.ReadAsStringAsync(timeout.Token);
                    LogRefused(endpoint.Host, status, reason.Length > 200 ? reason[..200] : reason);
                    return PushOutcome.Refused;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                status = 0;
            }
            catch (HttpRequestException)
            {
                status = 0;
            }

            if (attempt < options.Attempts)
            {
                await Task.Delay(delay, clock, cancellationToken);
                delay *= 2;
            }
        }

        LogFailed(endpoint.Host, status, options.Attempts);
        return PushOutcome.Failed;
    }

    /// <summary>The Declarative Web Push message (WebKit): <c>web_push: 8030</c>, a title, body, tap URL and tag.</summary>
    internal static string Payload(PushMessage message, Uri appOrigin) => new JsonObject
    {
        ["web_push"] = 8030,
        ["notification"] = new JsonObject
        {
            ["title"] = message.Title,
            ["body"] = message.Body,
            ["navigate"] = new Uri(appOrigin, message.Path).ToString(),
            ["tag"] = message.Topic,
            ["lang"] = "en-GB",
        },
    }.ToJsonString();

    private HttpRequestMessage Request(Uri endpoint, byte[] body, PushMessage message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.Authorization = new AuthenticationHeaderValue("vapid", $"t={VapidJwt(endpoint, clock.GetUtcNow())}, k={options.PublicKey}");
        request.Headers.Add("TTL", ((int)message.TimeToLive.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("Urgency", message.Urgency switch
        {
            PushUrgency.VeryLow => "very-low",
            PushUrgency.Low => "low",
            PushUrgency.High => "high",
            _ => "normal",
        });
        request.Headers.Add("Topic", message.Topic);
        return request;
    }

    /// <summary>RFC 8292: an ES256 JWT for the push service's origin, valid 12 hours (at most 24 are allowed), with the
    /// app's origin as the subject (Apple rejects made-up contacts with 403 BadJwtToken).</summary>
    internal string VapidJwt(Uri endpoint, DateTimeOffset now)
    {
        var header = B64.Encode(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        var claims = B64.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = endpoint.GetLeftPart(UriPartial.Authority),
            exp = now.AddHours(12).ToUnixTimeSeconds(),
            sub = options.AppOrigin.GetLeftPart(UriPartial.Authority),
        }));
        var publicKey = B64.Decode(options.PublicKey!);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B64.Decode(options.PrivateKey!),
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256); // r || s
        return $"{header}.{claims}.{B64.Encode(signature)}";
    }

    /// <summary>RFC 8291 encryption: one aes128gcm record (payloads are far below 4 KB). The key and salt parameters
    /// exist for the RFC's test vector.</summary>
    internal static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman? asKey = null, byte[]? salt = null)
    {
        using var ephemeral = asKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        salt ??= RandomNumberGenerator.GetBytes(16);
        var asParameters = ephemeral.ExportParameters(false);
        byte[] asPublic = [0x04, .. asParameters.Q.X!, .. asParameters.Q.Y!];
        using var ua = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var ecdhSecret = ephemeral.DeriveRawSecretAgreement(ua.PublicKey);

        byte[] keyInfo = [.. Encoding.ASCII.GetBytes("WebPush: info\0"), .. uaPublic, .. asPublic];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        byte[] padded = [.. plaintext, 0x02]; // the last (only) record's delimiter
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
        {
            aes.Encrypt(nonce, padded, cipher, tag);
        }

        // Header: salt (16) | record size (uint32, big-endian) | key id length (1) | key id = as_public (65)
        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return [.. header, .. cipher, .. tag];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push subscription at {PushService} is gone (HTTP {Status}).")]
    private partial void LogGone(string pushService, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push service {PushService} refused a push (HTTP {Status}): {Reason}")]
    private partial void LogRefused(string pushService, int status, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push to {PushService} failed after {Attempts} attempts (last HTTP {Status}; 0 = no response).")]
    private partial void LogFailed(string pushService, int status, int attempts);
}

/// <summary>Base64url without padding, as Web Push uses for keys.</summary>
internal static class B64
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}
