// Spike #75 proof of concept: send one push to every subscription the PoC page saved.
//
//   dotnet run -- --self-test                                   check the encryption against RFC 8291's test vector
//   dotnet run -- <env file outside the repo> [title] [body] [navigate]
//
// The env file holds VAPID_PUBLIC_KEY, VAPID_PRIVATE_KEY and VAPID_SUBJECT. Nothing secret is printed: only the push
// service's host and its status code (an endpoint URL is a capability, so it isn't printed either).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args is ["--self-test"])
{
    // RFC 8291 section 5 / appendix A.
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
        Encoding.UTF8.GetBytes("When I grow up, I want to be a watermelon"),
        B64.Decode("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
        B64.Decode("BTBZMqHH6r4Tts7J_aSIgg"),
        asKey,
        B64.Decode("DGv6ra1nlYgDCS1FRnbzlw"));
    const string expected =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";
    var ok = B64.Encode(body) == expected;
    Console.WriteLine(ok ? "RFC 8291 test vector: MATCH" : $"RFC 8291 test vector: MISMATCH\n  got {B64.Encode(body)}");
    return ok ? 0 : 1;
}

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run -- --self-test | <env file> [title] [body] [navigate]");
    return 1;
}

var env = File.ReadAllLines(args[0])
    .Where(l => l.Contains('='))
    .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..].Trim());
var store = Path.Combine(AppContext.BaseDirectory, "../../../../subscriptions.json");
var subscriptions = JsonDocument.Parse(File.ReadAllText(store)).RootElement.EnumerateArray().ToList();

// The Declarative Web Push format (WebKit, Safari 18.4+): shown by Safari even without the service worker, and by our
// service worker everywhere else. The tag makes a newer push replace an older one of the same kind on screen.
var payload = JsonSerializer.Serialize(new
{
    web_push = 8030,
    notification = new
    {
        title = args.ElementAtOrDefault(1) ?? "Use first today",
        body = args.ElementAtOrDefault(2) ?? "Push PoC: 2 things to use up. Tap to open.",
        navigate = args.ElementAtOrDefault(3) ?? "/?from=push",
        tag = "poc-use-first",
    },
});

using var http = new HttpClient();
var sender = new WebPushSender(http, env["VAPID_PUBLIC_KEY"], env["VAPID_PRIVATE_KEY"], env["VAPID_SUBJECT"]);
foreach (var s in subscriptions)
{
    var endpoint = s.GetProperty("endpoint").GetString()!;
    var keys = s.GetProperty("keys");
    var (outcome, status) = await sender.SendAsync(endpoint, keys.GetProperty("p256dh").GetString()!, keys.GetProperty("auth").GetString()!,
        payload, ttlSeconds: 60 * 60 * 12, topic: "poc-use-first", urgency: "normal", CancellationToken.None);
    Console.WriteLine($"{new Uri(endpoint).Host}: HTTP {status} → {outcome} (aes128gcm, {Encoding.UTF8.GetByteCount(payload)}-byte payload)");
}

return 0;
