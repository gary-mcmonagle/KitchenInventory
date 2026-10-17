# Spike #38: Azure SignalR (serverless) for the live scan feed

Card: https://trello.com/c/b6lOX2AO

## Question

How do we push scan results live to every open React app using **Azure SignalR Service, Free tier, serverless mode**, with our Function apps and Entra-authenticated users?

## Summary

- **Use Azure SignalR Service, Free tier, Serverless mode**, with the isolated worker's **class-based `ServerlessHub`**. Authenticate to it with **managed identity**, with local keys disabled, so there's no connection string or secret.
- **Two senders.** Ingest sends **`scanReceived`** the moment a scan is stored (the grey toast). The change-feed processor sends the outcome: **`scanIdentified`** (green), **`scanNeedsIdentification`** (yellow), **`checkOutUnmatched`** or **`scanSkipped`**. Every event is keyed by the **scan ID**.
- **Negotiate** is an HTTP function in the API app, protected by the Entra bearer-token middleware from spike #35. It negotiates with `UserId` = the user's `oid`. The React client passes the token with `accessTokenFactory`. Events are **broadcast to all connections**: every connection is an authenticated household member.
- **Free tier** (20 concurrent connections, 20,000 messages a day, no SLA) is far more than one household needs.
- **The feed is a live hint; the API is the source of truth.** On every (re)connect, and when the app comes back to the foreground, the UI fetches recent scans from the API to fill any gaps.
- **Sending is best-effort.** A SignalR failure is logged as a **warning** and never fails ingest or check-in.
- **Web PubSub** would also work, with the same Free limits, but SignalR fits our "server pushes, client listens" case with less code. Recommend SignalR.

## 1. Wiring (.NET isolated worker)

**Package:** `Microsoft.Azure.Functions.Worker.Extensions.SignalRService`. It has the input and output bindings and, for the isolated worker, the **class-based model** (`ServerlessHub` / `ServerlessHub<T>`), which Microsoft describes as "more flexible negotiation, sending messages and managing groups" than the bindings.

**Negotiate (API app).** One HTTP function in a hub class:

```csharp
[SignalRConnection("AzureSignalRConnectionString")]
public sealed class ScanFeed : ServerlessHub<IScanFeedClient>   // hub name = "ScanFeed"
{
    public ScanFeed(IServiceProvider sp) : base(sp) { }

    [Function("negotiate")]
    public async Task<HttpResponseData> Negotiate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "negotiate")] HttpRequestData req,
        FunctionContext context)
    {
        var user = context.GetCurrentUser();            // set by the #35 auth middleware; 401 before we get here
        var body = await NegotiateAsync(new() { UserId = user.ObjectId });
        var res = req.CreateResponse(HttpStatusCode.OK);
        res.Headers.Add("Content-Type", "application/json");
        await res.WriteBytesAsync(body.ToArray());
        return res;
    }
}
```

We **don't** use the `[SignalRConnectionInfoInput(UserId = "{headers.x-ms-client-principal-id}")]` binding. That header comes from App Service built-in auth, which spike #35 decided against. `NegotiateAsync` takes the user ID straight from our validated token.

**Sending (both apps).** The Application layer calls an interface (see [section 6](#6-clean-architecture)). Its Infrastructure implementation sends to the hub through the SignalR **Management SDK** (`Microsoft.Azure.SignalR.Management`: a `ServiceHubContext` for hub `ScanFeed`). It uses the same identity-based settings, and works from a change-feed trigger, an HTTP trigger or a timer alike. Output bindings would tie sending to the function signature, and leak SignalR types into the adapter.

**Which app sends what, and when:**

| When | Sent by | Event | Toast |
|---|---|---|---|
| Ingest has stored a new scan event (after the Cosmos create, before the 2xx) | **Ingest** | `scanReceived` | Grey (loading) |
| Check-in: the product is `known` and the unit is created | **Change-feed processor** | `scanIdentified` (`outcome: checkedIn`) | Green, product name |
| Check-in: the product is `noMatch` / `lookupFailed` and the unit is created | Change-feed processor | `scanNeedsIdentification` | Yellow, tap to open photo identification |
| Check-out: a unit was checked out | Change-feed processor | `scanIdentified` (`outcome: checkedOut`) | Green, product name |
| Check-out: no unit in stock (an unmatched check-out) | Change-feed processor | `checkOutUnmatched` | The scan feed feature picks the look from the mockup |
| Store-labelled barcode (skipped for now) | Change-feed processor | `scanSkipped` (`reason: storeLabel`) | Grey toast removed, or a short note |

Why ingest sends the grey event: the change-feed trigger polls (by default every 5 seconds; `FeedPollDelay` can lower it), and an Open Food Facts lookup takes ~100 ms. Sent from the processor, the grey toast would appear seconds late and then flash. Sent from ingest, it appears within about a second of the scan. **A resent scan** (ingest's `id` conflict) doesn't send `scanReceived` again.

**Ordering:** the two apps send independently, so a processor event can, rarely, arrive before `scanReceived`. The UI keys toasts by `scanId` and **never moves a toast backwards**: a final state (identified, needs identification, unmatched, skipped) is never replaced by `scanReceived`.

**Replays:** a change-feed replay that finds the unit already exists sends the outcome event again. That's harmless, because the UI updates the toast with that `scanId` in place.

## 2. Auth

- **Negotiate is protected by our bearer token.** `POST /api/negotiate` goes through the #35 middleware (signature, issuer, audience, tenant, `azp`, `scp`), so only signed-in household members get a SignalR connection. No token → 401.
- **The React client:**

  ```ts
  const connection = new HubConnectionBuilder()
    .withUrl(`${apiBaseUrl}/api`, {
      accessTokenFactory: async () => (await msal.acquireTokenSilent({ scopes: [apiScope], account })).accessToken,
      withCredentials: false,           // bearer token only, no cookies, so no credentialed CORS
    })
    .withAutomaticReconnect([0, 2000, 10000, 30000])
    .build();
  connection.on("scanReceived", onScanReceived);  // …one handler per event
  ```

  The client calls `{url}/negotiate` with `Authorization: Bearer <Entra token>`. The response contains the SignalR Service URL and a **SignalR access token**, which the client then uses to connect to the service. Every reconnect negotiates again, so `accessTokenFactory` is called again and MSAL supplies a fresh Entra token.
- **CORS:** negotiate lives on the API Function app, so the Function app's CORS (spike #35: the Static Web App origin) already covers it. `withCredentials: false` means no `supportCredentials` is needed. The SignalR resource's own CORS is set to the same origins.
- **Targeting: broadcast to all.** Every connection belongs to an invited household member, and everyone should see every scan. No per-user or group targeting. Negotiate still sets `UserId`, so a later feature can target a person (e.g. "your check-out was undone") without changing negotiate.
- **The SignalR service's own auth:** **managed identity**, with `disableLocalAuth: true` on the resource, so there's no access key or connection string anywhere. Both Function apps' system-assigned identities get the **SignalR Service Owner** role on the SignalR resource (the role the Functions docs name for identity-based connections).

## 3. Free tier limits

| | Free (F1) | Our expected use |
|---|---|---|
| Concurrent connections | **20** per unit, 1 unit | 2–4 phones and laptops |
| Messages per day | **20,000** | ~2–3 per scan × tens of scans ≈ < 200 a day, plus a stock-take burst of a few hundred |
| SLA | **None** | Fine: the feed is a hint, and the API is the source of truth |
| Upgrade | Free → Standard changes the public IP and causes **30–60 minutes of downtime** | Unlikely to be needed |

Message counting (Microsoft's "Messages and connections" doc): only **outbound** messages count, a broadcast counts **once per receiving client**, and messages over 2 KB count as several 2 KB messages. Our events are a few hundred bytes. Even five open phones and a 300-scan stock-take (~1,000 events × 5 connections = 5,000 messages) stay well under 20,000.

**Watch the connection count.** "Each application server starts with five initial connections per hub" when it keeps persistent server connections. Two Function apps (and scaled-out instances) in persistent mode could use half or more of the Free tier's 20 connections. Our apps only **send**, so use the **transient (REST API) transport**, which holds no server connections: `ServiceTransportType.Transient` in the Management SDK, and set `AzureSignalRServiceTransportType=Transient` explicitly in the Function apps' settings rather than relying on a default. Then only the phones and laptops count.

## 4. Message contract

Hub `ScanFeed`. Every event is a JSON object with `scanId` (the scan event's `id`), `deviceId`, `role` and `occurredAt` (UTC). Names follow `docs/domain-language.md`.

```ts
// Ingest
type ScanReceived = {
  scanId: string; deviceId: string; role: "in" | "out";
  barcode: string; scannedAt: string; occurredAt: string;
};

// Change-feed processor
type ScanIdentified = {
  scanId: string; deviceId: string; role: "in" | "out"; occurredAt: string;
  outcome: "checkedIn" | "checkedOut";
  productId: string; displayName: string; brand: string | null; size: string | null;
  unitId: string;
};

type ScanNeedsIdentification = {           // check-in of a no-match or lookup-failed product
  scanId: string; deviceId: string; role: "in"; occurredAt: string;
  productId: string; barcode: string;
  displayName: string;                     // "Unknown item (<barcode>)"
  productStatus: "noMatch" | "lookupFailed";
  unitId: string;
};

type CheckOutUnmatched = {                 // check-out with no unit in stock
  scanId: string; deviceId: string; role: "out"; occurredAt: string;
  barcode: string; productId: string | null; displayName: string | null;
};

type ScanSkipped = {
  scanId: string; deviceId: string; role: "in" | "out"; occurredAt: string;
  barcode: string; reason: "storeLabel";
};
```

- **Client method names** (`connection.on(...)`) are the event names in camelCase: `scanReceived`, `scanIdentified`, `scanNeedsIdentification`, `checkOutUnmatched`, `scanSkipped`.
- Server side, these are records in Application (`ScanFeedEvent` subtypes), serialised with camelCase JSON.
- **No personal data** beyond product names and barcodes. Neither `userId` nor email is in any event.
- **Versioning:** add fields, never rename. If a breaking change is ever needed, use a new event name.

## 5. Reconnection and gaps

- **Phone sleeps or the tab is backgrounded:** mobile browsers suspend background tabs and drop WebSockets. `withAutomaticReconnect` retries at 0 s, 2 s, 10 s and 30 s, then gives up (`onclose`).
- **Also listen for `visibilitychange`.** When the page becomes visible and the connection isn't `Connected`, call `start()` again (which negotiates again, with a fresh Entra token). A short retry loop handles start failures.
- **Fill gaps from the API, always.** After every successful (re)connect, and when the page becomes visible, call **`GET /api/scans/recent?since=<oldest toast or now − 10 min>`**. It returns recent scan events with their current outcome (the same shapes as the events above, built from `scanEvents` + units + products + unmatched check-outs). Merge by `scanId`. Events missed while asleep then appear, and toasts stuck on grey resolve.
- The **stock list** reloads too, since check-ins and check-outs may have happened while away.
- This endpoint is part of the scan feed feature. It's a cross-partition query over a few minutes of scan events, which is cheap.

## 6. Clean architecture

```csharp
// Application
public interface IScanFeedPublisher
{
    Task PublishAsync(ScanFeedEvent feedEvent, CancellationToken cancellationToken);
}

public abstract record ScanFeedEvent(string ScanId, string DeviceId, Role Role, DateTimeOffset OccurredAt);
public sealed record ScanReceived(...) : ScanFeedEvent(...);
public sealed record ScanIdentified(...) : ScanFeedEvent(...);
public sealed record ScanNeedsIdentification(...) : ScanFeedEvent(...);
public sealed record CheckOutUnmatched(...) : ScanFeedEvent(...);
public sealed record ScanSkipped(...) : ScanFeedEvent(...);
```

- **Use cases publish:** `RecordScan` (ingest) publishes `ScanReceived`; `CheckInScan` / `CheckOutScan` (processor) publish the outcome. They call the publisher **after** their writes succeed.
- **Best-effort:** the use case catches publisher exceptions, logs a **warning** (scan ID and event name), and carries on. The feed is never allowed to fail a check-in. Warnings don't trigger the error alert (spike #39 / #48).
- **Infrastructure:** `SignalRScanFeedPublisher` maps each record to its client method name and payload and sends with `ServiceHubContext.Clients.All.SendCoreAsync(...)`, built with **`ServiceTransportType.Transient`** (REST calls, no persistent server connections; see [section 3](#3-free-tier-limits)). It's registered in both apps from the shared Infrastructure project.
- **Tests:** use cases with a fake publisher (correct event per outcome, published after the write, a publisher exception doesn't fail the use case). A small mapping test for the Infrastructure serialiser (names and JSON shape match the contract). The negotiate function with a fake `CurrentUser` sets `UserId`.

## 7. Alternatives: Azure Web PubSub

| | **SignalR Service (Serverless)** | Web PubSub |
|---|---|---|
| Free tier | 20 connections, 20,000 messages a day, no SLA | Same: 20 connections, 20,000 messages a day, no SLA |
| Functions integration | Mature bindings plus the isolated-worker `ServerlessHub` class model | Bindings exist (`WebPubSubConnection`, `WebPubSub` output) |
| Client | `@microsoft/signalr`: negotiate, transport fallback and **automatic reconnect** built in | Plain WebSocket or `@azure/web-pubsub-client` (has reconnect); more protocol choices to make |
| Fit | Server pushes, clients listen: exactly our case | Better for raw WebSocket or pub/sub between clients, which we don't need |

**Recommend SignalR Service.** Same cost, less code, and the client library handles negotiate and reconnect for us.

## 8. Bicep

```bicep
resource signalR 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: signalRName
  location: location
  kind: 'SignalR'
  sku: { name: 'Free_F1', capacity: 1 }
  properties: {
    features: [
      { flag: 'ServiceMode', value: 'Serverless' }
      { flag: 'EnableConnectivityLogs', value: 'true' }
    ]
    cors: { allowedOrigins: [ staticWebAppOrigin ] }   // plus a custom domain later
    disableLocalAuth: true                             // managed identity only, no access keys
    publicNetworkAccess: 'Enabled'
  }
}
```

- **Role assignments:** *SignalR Service Owner* on this resource for the **ingest** and **API** Function apps' system-assigned identities.
- **App settings (both apps, identity-based, no secrets):**
  - `AzureSignalRConnectionString__serviceUri` = `https://<name>.service.signalr.net`
  - `AzureSignalRConnectionString__credential` = `managedidentity`
  - `AzureSignalRServiceTransportType` = `Transient` (REST, no persistent server connections eating the Free tier's 20)
- No upstream endpoints: clients never send messages to the server.
- Diagnostic settings to the existing Log Analytics workspace, if the Bicep baseline sets that pattern up.
- Pick the API version when writing the Bicep. The one above is illustrative.

## Local development

- **SignalR emulator** for serverless mode: `dotnet tool install -g Microsoft.Azure.SignalR.Emulator`, then `asrs-emulator start`. Point `AzureSignalRConnectionString` in `local.settings.json` (git-ignored) at the emulator's connection string. Both Function apps run locally (`func start`), and the UI on Vite connects through the local API's negotiate.
- **Or** use the deployed Free instance with your own `az login` (it would need *SignalR Service Owner* for Gary's account too). The emulator keeps local scans off the live household feed, so **prefer the emulator**.
- Unit tests need neither (see [section 6](#6-clean-architecture)).

## Recommended design

```mermaid
sequenceDiagram
    autonumber
    participant Pi as Pi (hub)
    participant Ingest as Ingest Function
    participant Cosmos as Cosmos (scanEvents, products, units)
    participant Proc as API Function: change-feed processor
    participant OFF as Open Food Facts
    participant SR as SignalR Service (Free, Serverless)
    participant API as API Function: negotiate / recent scans
    participant UI as React app (phones)

    UI->>API: POST /api/negotiate (Bearer Entra token)
    API-->>UI: SignalR URL + access token (UserId = oid)
    UI->>SR: Connect (WebSocket)

    Pi->>Ingest: POST /api/scans (scan ID, barcode, deviceId)
    Ingest->>Cosmos: create scan event (id = scan ID)
    Ingest-)SR: scanReceived (best-effort)
    SR-)UI: scanReceived → grey toast
    Ingest-->>Pi: 2xx

    Cosmos-)Proc: change feed (new scan event)
    Proc->>Cosmos: barcode lookup / product
    opt new barcode
        Proc->>OFF: product lookup
    end
    Proc->>Cosmos: create unit / check out
    Proc-)SR: scanIdentified | scanNeedsIdentification | checkOutUnmatched (best-effort)
    SR-)UI: → green / yellow toast (by scan ID)

    Note over UI: Phone wakes or reconnects
    UI->>API: GET /api/scans/recent?since=… (Bearer)
    API-->>UI: recent scans with outcomes → merge by scan ID
```

## Recommendation

1. **Azure SignalR Service, Free_F1, Serverless**, with local auth disabled. Both Function apps connect with managed identity (*SignalR Service Owner*).
2. **Negotiate** in the API app, behind the #35 bearer-token middleware, using `ServerlessHub.NegotiateAsync(UserId = oid)`. Broadcast to all.
3. **Ingest** sends `scanReceived`; the **change-feed processor** sends the outcome. Everything is keyed by scan ID, best-effort, behind `IScanFeedPublisher`.
4. **React:** `@microsoft/signalr` with `accessTokenFactory` (MSAL), `withAutomaticReconnect`, a `visibilitychange` restart, and **`GET /api/scans/recent`** after every (re)connect to fill gaps.
5. **Web PubSub:** not needed.

## Suggested follow-up cards (not created)

- **Notes for [Feature] Live scan feed on the stock list** (no new card): build to this contract. It includes `GET /api/scans/recent`, the SignalR resource and role assignments in Bicep, and the ingest change to send `scanReceived`. Map events to the mockup's toast states.
- **[Task] SignalR Service in Bicep with managed identity:** only if the scan feed feature is too big. It would split out the resource, `disableLocalAuth`, role assignments and app settings.

## Sources

- Azure Functions SignalR Service bindings (isolated package, identity-based connections, *SignalR Service Owner*, `AzureSignalRConnectionString__serviceUri` / `__credential`): https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-signalr-service
- Serverless development and configuration (Serverless mode required, `negotiate`, the class-based `ServerlessHub` and `NegotiateAsync(NegotiationOptions)`, client negotiation, CORS): https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-concept-serverless-development-config
- SignalR Service pricing (Free: 20 connections, 20,000 messages per unit per day, 1 unit, no SLA): https://azure.microsoft.com/en-us/pricing/details/signalr-service/
- Messages and connections (outbound-only billing, per-client counting, 2 KB chunks, 5 server connections per hub per app server): https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-concept-messages-and-connections
- Scaling SignalR (Free ↔ Standard downtime of 30–60 minutes): https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-howto-scale-signalr
- Web PubSub pricing (Free: 20 connections, 20,000 messages per day, no SLA): https://azure.microsoft.com/en-us/pricing/details/web-pubsub/
- SignalR JavaScript client (`accessTokenFactory`, `withAutomaticReconnect`): https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client
- Class-based isolated sample: https://github.com/aspnet/AzureSignalR-samples/tree/main/samples/DotnetIsolated-ClassBased/
