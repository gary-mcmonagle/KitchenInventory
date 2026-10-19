using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Push;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.Push;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.Tests;

public sealed class WebPushSenderTests
{
    // A throwaway VAPID pair generated for these tests only (not used anywhere else).
    private static readonly (string Public, string Private) Vapid = MakeKeys();

    // A browser subscription's keys (RFC 8291's user agent key and auth secret).
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UaAuth = "BTBZMqHH6r4Tts7J_aSIgg";

    private static readonly WebPushOptions Options = new(Vapid.Public, Vapid.Private, new Uri("https://kitchen.garymcmonagle.com"))
    {
        FirstRetryDelay = TimeSpan.Zero,
    };

    private static readonly PushMessage Message = new("Cupboard updated", "Added Milk", "/?from=push", "stock", TimeSpan.FromHours(12));

    private static (string, string) MakeKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        return (B64.Encode([0x04, .. p.Q.X!, .. p.Q.Y!]), B64.Encode(p.D!));
    }

    private static PushSubscription Subscription(string endpoint = "https://web.push.apple.com/QGuQyavXutnMH") =>
        new("id", Guid.NewGuid(), endpoint, UaPublic, UaAuth, "iPhone", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null,
            new Dictionary<string, bool>());

    private sealed class Handler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int next;

        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            var status = statuses[Math.Min(next++, statuses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(status == HttpStatusCode.Forbidden ? """{"reason":"BadJwtToken"}""" : "") };
        }
    }

    private sealed class Logs : ILogger<WebPushSender>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static (WebPushSender Sender, Handler Handler, Logs Logs) Make(WebPushOptions options, params HttpStatusCode[] statuses)
    {
        var handler = new Handler(statuses);
        var logs = new Logs();
        return (new WebPushSender(new HttpClient(handler), options, TimeProvider.System, logs), handler, logs);
    }

    [Fact]
    public void Encryption_reproduces_the_RFC_8291_test_vector_byte_for_byte()
    {
        using var asKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B64.Decode("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new ECPoint
            {
                X = B64.Decode("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8")[1..33],
                Y = B64.Decode("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8")[33..65],
            },
        });

        var body = WebPushSender.Encrypt(
            Encoding.UTF8.GetBytes("When I grow up, I want to be a watermelon"), B64.Decode(UaPublic), B64.Decode(UaAuth), asKey,
            B64.Decode("DGv6ra1nlYgDCS1FRnbzlw"));

        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            B64.Encode(body));
    }

    [Fact]
    public void The_VAPID_JWT_names_the_push_service_a_real_subject_and_a_short_expiry_and_verifies_with_the_public_key()
    {
        var (sender, _, _) = Make(Options);
        var now = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

        var jwt = sender.VapidJwt(new Uri("https://web.push.apple.com/QGuQyavXutnMH"), now);

        var parts = jwt.Split('.');
        Assert.Equal("""{"typ":"JWT","alg":"ES256"}""", Encoding.UTF8.GetString(B64.Decode(parts[0])));
        var claims = JsonNode.Parse(B64.Decode(parts[1]))!;
        Assert.Equal("https://web.push.apple.com", claims["aud"]!.GetValue<string>());
        Assert.Equal("https://kitchen.garymcmonagle.com", claims["sub"]!.GetValue<string>()); // Apple: a real subject
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims["exp"]!.GetValue<long>());
        var publicKey = B64.Decode(Vapid.Public);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B64.Decode(parts[2]), HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task Sends_aes128gcm_with_vapid_ttl_urgency_and_topic()
    {
        var (sender, handler, _) = Make(Options, HttpStatusCode.Created);

        Assert.Equal(PushOutcome.Delivered, await sender.SendAsync(Subscription(), Message with { Urgency = PushUrgency.High }, CancellationToken.None));

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://web.push.apple.com/QGuQyavXutnMH", request.RequestUri!.ToString());
        Assert.Equal(["aes128gcm"], request.Content!.Headers.ContentEncoding);
        Assert.Equal("vapid", request.Headers.Authorization!.Scheme);
        Assert.EndsWith($", k={Vapid.Public}", request.Headers.Authorization.Parameter, StringComparison.Ordinal);
        Assert.Equal(["43200"], request.Headers.GetValues("TTL"));
        Assert.Equal(["high"], request.Headers.GetValues("Urgency"));
        Assert.Equal(["stock"], request.Headers.GetValues("Topic"));
        Assert.Equal(4096u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4))); // aes128gcm header
        Assert.Equal(65, body[20]);
    }

    [Fact]
    public void The_payload_is_declarative_web_push_with_an_absolute_tap_url_and_the_topic_as_tag()
    {
        var payload = JsonNode.Parse(WebPushSender.Payload(Message, new Uri("https://kitchen.garymcmonagle.com")))!;

        Assert.Equal(8030, payload["web_push"]!.GetValue<int>());
        Assert.Equal("Cupboard updated", payload["notification"]!["title"]!.GetValue<string>());
        Assert.Equal("Added Milk", payload["notification"]!["body"]!.GetValue<string>());
        Assert.Equal("https://kitchen.garymcmonagle.com/?from=push", payload["notification"]!["navigate"]!.GetValue<string>());
        Assert.Equal("stock", payload["notification"]!["tag"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, PushOutcome.Gone, 1)]
    [InlineData(HttpStatusCode.Gone, PushOutcome.Gone, 1)]
    [InlineData(HttpStatusCode.Forbidden, PushOutcome.Refused, 1)]
    [InlineData(HttpStatusCode.BadRequest, PushOutcome.Refused, 1)]
    [InlineData(HttpStatusCode.TooManyRequests, PushOutcome.Failed, 3)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PushOutcome.Failed, 3)]
    public async Task Answers_map_to_outcomes_and_only_throttling_and_server_errors_are_retried(HttpStatusCode status, PushOutcome outcome, int calls)
    {
        var (sender, handler, _) = Make(Options, status);

        Assert.Equal(outcome, await sender.SendAsync(Subscription(), Message, CancellationToken.None));
        Assert.Equal(calls, handler.Requests.Count);
    }

    [Fact]
    public async Task A_retry_that_succeeds_is_delivered()
    {
        var (sender, handler, _) = Make(Options, HttpStatusCode.TooManyRequests, HttpStatusCode.Created);
        Assert.Equal(PushOutcome.Delivered, await sender.SendAsync(Subscription(), Message, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Never_sends_to_other_hosts_or_with_a_bad_topic_and_says_when_not_configured()
    {
        var (sender, handler, _) = Make(Options, HttpStatusCode.Created);
        Assert.Equal(PushOutcome.Refused, await sender.SendAsync(Subscription("https://evil.example/push"), Message, CancellationToken.None));
        Assert.Equal(PushOutcome.Refused, await sender.SendAsync(Subscription(), Message with { Topic = "use-first" }, CancellationToken.None));
        Assert.Empty(handler.Requests);

        var (unconfigured, none, _) = Make(Options with { PrivateKey = null }, HttpStatusCode.Created);
        Assert.Equal(PushOutcome.NotConfigured, await unconfigured.SendAsync(Subscription(), Message, CancellationToken.None));
        Assert.Empty(none.Requests);
    }

    [Fact]
    public async Task Logs_hold_the_host_and_reason_never_the_endpoint_keys_or_payload()
    {
        var (sender, _, logs) = Make(Options, HttpStatusCode.Forbidden);

        await sender.SendAsync(Subscription(), Message, CancellationToken.None);

        var entry = Assert.Single(logs.Messages);
        Assert.Contains("web.push.apple.com", entry, StringComparison.Ordinal);
        Assert.Contains("BadJwtToken", entry, StringComparison.Ordinal);
        foreach (var secret in new[] { "QGuQyavXutnMH", Vapid.Private, Vapid.Public, UaAuth, "Added Milk" })
        {
            Assert.DoesNotContain(secret, entry, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Options_come_from_app_settings_and_an_unresolved_key_vault_reference_counts_as_not_configured()
    {
        static WebPushOptions From(Dictionary<string, string?> values) =>
            WebPushOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        var configured = From(new() { ["VAPID_PUBLIC_KEY"] = "pub", ["VAPID_PRIVATE_KEY"] = "priv", ["PUSH_APP_ORIGIN"] = "https://example.test/path" });
        Assert.Equal(("pub", "priv", "https://example.test/"), (configured.PublicKey, configured.PrivateKey, configured.AppOrigin.ToString()));
        Assert.True(configured.IsConfigured);

        var reference = From(new() { ["VAPID_PUBLIC_KEY"] = "pub", ["VAPID_PRIVATE_KEY"] = "@Microsoft.KeyVault(VaultName=kv;SecretName=vapid-private-key)" });
        Assert.False(reference.IsConfigured);
        Assert.Equal("https://kitchen.garymcmonagle.com/", reference.AppOrigin.ToString());
    }
}

public sealed class PushStorageTests
{
    [Fact]
    public void Subscription_documents_round_trip()
    {
        var subscription = new PushSubscription("abc", Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "https://fcm.googleapis.com/fcm/send/x",
            "p", "a", "Mac", new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero),
            null, new Dictionary<string, bool> { ["stockChanges"] = true });

        var document = CosmosPushSubscriptionRepository.ToDocument(subscription);

        Assert.Equal("caf87c93-9030-44e8-a25d-fed63261ea67", document["userId"]!.GetValue<string>());
        Assert.Equal("p", document["keys"]!["p256dh"]!.GetValue<string>());
        var back = CosmosPushSubscriptionRepository.FromDocument(JsonNode.Parse(document.ToJsonString())!.AsObject())!;
        Assert.Equal(subscription with { Preferences = back.Preferences }, back);
        Assert.Equal(subscription.Preferences, back.Preferences);
    }

    [Fact]
    public void Receipts_are_one_per_event_and_person_and_carry_a_ttl()
    {
        var user = Guid.NewGuid();
        var receipt = CosmosPushSeenRepository.Receipt("scan:123", user, DateTimeOffset.UnixEpoch);

        Assert.Equal($"scan:123:{user}", receipt["id"]!.GetValue<string>());
        Assert.Equal("scan:123", receipt["eventKey"]!.GetValue<string>());
        Assert.Equal(86400, receipt["ttl"]!.GetValue<int>());
    }

    [Fact]
    public void Push_jobs_survive_the_queue_round_trip()
    {
        var job = new PushJob(Guid.NewGuid(), new PushMessage("T", "B", "/", "stock", TimeSpan.FromMinutes(30), PushUrgency.High),
            new PushAudience { OptedInTo = "stockChanges", ExceptSeenEventKey = "scan:1", ExceptUsers = [Guid.NewGuid()] });

        var back = StorageQueuePushJobScheduler.Deserialize(StorageQueuePushJobScheduler.Serialize(job))!;

        Assert.Equal(job.JobId, back.JobId);
        Assert.Equal(job.Message, back.Message);
        Assert.Equal((job.Audience.OptedInTo, job.Audience.ExceptSeenEventKey), (back.Audience.OptedInTo, back.Audience.ExceptSeenEventKey));
        Assert.Equal(job.Audience.ExceptUsers, back.Audience.ExceptUsers);
    }
}
