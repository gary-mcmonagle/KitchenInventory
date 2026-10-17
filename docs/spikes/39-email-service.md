# Spike #39: Email service for alerts and the daily digest

Card: https://trello.com/c/S4iMZP52

## Question

Which email service should the Function apps use to send the **offline alert**, the **error alert** and the **9pm daily digest**, and how do we wire it in serverlessly?

## Summary

- **Error alerts go through Azure Monitor**, not our code: a log search alert rule plus an action group that emails Gary. No email service is needed for them, and they still work when our app is the thing that's broken.
- **The offline alert and the daily digest go through our `INotifier`**, with **Azure Communication Services (ACS) Email on an Azure-managed domain**: no domain to buy, **no secrets** (managed identity), defined in Bicep, and about a penny a month.
- **Big caveat: Microsoft is retiring ACS Email on 30 September 2028.** It's in maintenance mode, and Microsoft recommends against onboarding new workloads. We'd get about two years from it. `INotifier` keeps the swap to a different provider to one Infrastructure class and some Bicep. This is a **decision for Gary** (see [Decisions](#decisions-for-gary)); the alternative is Resend with a domain Gary owns.
- **`WEBSITE_TIME_ZONE` isn't supported on Linux Flex Consumption.** Schedule the digest timer at **20:00 and 21:00 UTC**, and let only the run where it's 21:00 in `Europe/London` send.
- Deliverability from the Azure-managed domain wasn't tested (no proof of concept in the live subscription). The first email feature checks it.

## Options

Volume: one recipient (Gary), a daily digest plus the odd offline alert. Roughly 30–40 emails a month.

| Option | Cost at our volume | Bicep | Auth | Setup | Notes |
|---|---|---|---|---|---|
| **ACS Email, Azure-managed domain** | ~$0.01/month ($0.00025 per email + $0.00012 per MB) | Yes (`Microsoft.Communication/emailServices`, `domains`, `communicationServices`) | **Managed identity** (Entra ID), so no secret | Minutes: no DNS | Sender is `DoNotReply@<guid>.azurecomm.net` and can't be changed. Limits 5/minute and 10/hour per subscription, can't be raised. **Retiring 30 Sep 2028**; maintenance mode only. |
| ACS Email, custom domain | Same | Yes, plus DNS records | Managed identity | Needs a domain Gary owns, and DNS verification | Own sender address and higher limits (30/min, 100/hour). Same retirement. |
| **Resend** | Free (3,000/month, 100/day) | No (an API key in Key Vault, sent over HTTPS) | **API key** (a secret) | Needs a **verified domain**: `resend.dev` only sends to the account owner's own address, for testing | Long-lived, simple REST API. Needs a domain and the Key Vault card first. |
| Brevo | Free (300/day) | No | API key (a secret) | Domain authentication recommended | Marketing-focused product; more account setup than we need. |
| SendGrid | **No free plan** since 2025 (60-day trial, then from $19.95/month) | No | API key | Domain authentication | Too expensive for a few emails a month. |
| Existing mailbox (Gmail SMTP) | Free | No | **App password**, which grants access to Gary's whole mailbox | 2-step verification and an app password | Excellent deliverability (it's from Gary to Gary), but a leaked app password exposes Gary's personal email. Not acceptable for a public repo's system. |
| Azure Monitor action group (alerts only) | 1,000 emails/month free | Yes | n/a (Azure sends it) | Minutes | Only sends **alert** emails with Azure's own layout, so it can't send the digest. Used for error alerts (below). |

Microsoft's own suggested replacements for ACS Email, **Microsoft 365 High Volume Email** (internal recipients only) and **Exchange Online** (a licensed mailbox), both need Microsoft 365, which a personal Gmail user doesn't have.

### Why ACS Email despite the retirement

- **No secrets.** The API Function app's managed identity sends the email. Nothing goes in Key Vault, so the offline alert (#44) doesn't have to wait for [Task] Key Vault and managed identity for secrets, and #44's "if it needs a secret, stop" rule doesn't trigger.
- **No domain** to buy, renew or set up DNS for.
- **All in Bicep**, in Gary's own subscription, for effectively nothing.
- **Two years is long enough** for a household app to prove itself, and the provider sits behind `INotifier`. Moving to Resend later means one new Infrastructure class (an HTTP call), a Key Vault secret, and removing three Bicep resources.

If Gary would rather not build on a retiring service, the choice is **Resend + a domain he owns + Key Vault first**.

## Deliverability

**Not tested.** A proof of concept would mean creating resources in the live subscription outside a card, so this spike doesn't build one.

- ACS sets up SPF, DKIM and DMARC for the Azure-managed domain automatically. That's the baseline Gmail needs, but a shared `azurecomm.net` sender has no reputation of its own, so the first emails may land in **Spam**.
- **How to handle it:** [Feature] Email alert when the Pi goes offline (#44) is the first feature to send email. Its verification should ask Gary whether the email reached the inbox or Spam. If it went to Spam, Gary marks it "Not spam" and adds a Gmail filter for the sender address ("Never send it to Spam"). With one recipient, that's a one-off fix.
- Azure Monitor alert emails come from `azure-noreply@microsoft.com`, which has a strong reputation.

## Error alerts: Azure Monitor or our notifier?

| Email | Sent by | Why |
|---|---|---|
| **Error alert** | **Azure Monitor**: a log search alert rule on Application Insights plus an action group | It must work when our code is the thing failing (a crash on startup, a broken deploy, a bad setting), so it can't depend on that code. No application code. The 1,000 free emails a month cover it. Defined in Bicep. Matches [Feature] Email alert when errors occur in Azure (#48). |
| **Offline alert** | **Our app** (`INotifier`, timer) | Needs app state: the hub's `lastSeenAt` and the one-email-per-outage flag. Already designed that way in #44. |
| **Daily digest** | **Our app** (`INotifier`, timer) | It's content, not an alert. |

The two paths back each other up. If the app fails to send the offline alert or the digest, it logs an **Error**, and the Azure Monitor rule emails Gary about that.

Costs: a log search alert rule at a 15-minute frequency is about $0.50/month (third-party figure; Microsoft's pricing page didn't render the number), and action group emails are free up to 1,000 a month (Microsoft's pricing page).

## The 9pm digest timer

- Timer triggers use **UTC** by default. `WEBSITE_TIME_ZONE` / `TZ` would fix that, but Microsoft's docs say they "aren't currently supported when running on Linux in a Flex Consumption or Consumption plan", and setting them "can create SSL-related issues and cause metrics to stop working".
- **So: schedule in UTC and check.** 21:00 in the UK is 20:00 UTC in summer (BST) and 21:00 UTC in winter (GMT):
  - Schedule: `0 0 20,21 * * *` (20:00 and 21:00 UTC every day).
  - On each run, convert the current time (from an injected `TimeProvider`) to `Europe/London`. **Only the run where the UK hour is 21 sends**; the other run logs "not 9pm in the UK, skipping" and returns.
  - On clock-change days this still gives exactly one 21:00 UK run, and the digest window (21:00 yesterday to 21:00 today, UK time) is 23 or 25 hours, as #46 expects.
  - The sent-digest record (keyed by the window's end date) stops a second send if the timer fires twice.
- **Time zone data:** use `TimeZoneInfo.FindSystemTimeZoneById("Europe/London")`. .NET on Linux reads the OS time zone database. Resolve it **at startup** so a missing zone fails loudly, and unit test the conversion on the last Sundays of March and October.
- **Retries:** add `[ExponentialBackoffRetry]` (or `[FixedDelayRetry]`) to the timer function. Microsoft documents this for isolated-worker timer triggers, but warns retries "can be less than the maximum" and don't move to another instance. So the last failure must also log an **Error**, which the Azure Monitor error alert picks up.
- The offline-alert timer (every 5 minutes) doesn't care about time zones; only its email text shows UK time.

## Templates

No template engine. The digest is a few short sections.

- A **pure builder** in Application, e.g. `DailyDigestEmailBuilder.Build(DigestData) → EmailContent(Subject, Html, PlainText)`. It's plain C#: `StringBuilder`, raw string literals for the HTML wrapper, and **`WebUtility.HtmlEncode`** on every product name (Open Food Facts data is untrusted). There are no third-party types, so it's allowed in Application.
- **Inline styles only**, one column, no images, system fonts. Email clients strip `<style>` blocks and web fonts, and the digest has to read well on a phone in light and dark mode.
- The **plain-text** version is built from the same data, not by stripping the HTML.
- **Unit tests** call the builder with fixed data and assert on the subject, sections, escaping (`<`, `&`, quotes) and the plain-text version. A snapshot test (approving the whole output) is optional.
- The offline alert uses the same `EmailContent` shape with a tiny builder.

## Clean architecture: the notifier

```csharp
// Application
public interface INotifier
{
    Task SendAsync(Notification notification, CancellationToken cancellationToken);
}

public sealed record Notification(NotificationKind Kind, EmailContent Content);

public sealed record EmailContent(string Subject, string Html, string PlainText);

public enum NotificationKind { OfflineAlert, DailyDigest }
```

- **Recipients aren't in Application.** Infrastructure maps the `NotificationKind` to an address from config (`ALERT_EMAIL_TO`, `DIGEST_EMAIL_TO`), so use cases never handle addresses and tests never need real ones.
- **Infrastructure:** `AcsEmailNotifier` uses `Azure.Communication.Email`'s `EmailClient` with the ACS endpoint and a `DefaultAzureCredential` (the managed identity in Azure, your `az login` locally). It sends with `WaitUntil.Started` (no polling for delivery), and a failure throws so the caller's retry and error logging apply.
- Swapping provider means a new `INotifier` implementation and a DI change. No use case changes.
- Error alerts don't go through `INotifier` at all.

## What the first email card sets up (Bicep)

For #44 to build. None of it is a secret.

- `Microsoft.Communication/emailServices` (location `global`, `dataLocation` in Europe or the UK), with a `domains` child named `AzureManagedDomain` (`domainManagement: 'AzureManaged'`).
- `Microsoft.Communication/communicationServices` (same data location) with `linkedDomains` set to that domain.
- A **role assignment** that lets the API Function app's managed identity send email. Microsoft's docs don't list a minimal built-in role. Community answers use a **custom role** (starting with `Microsoft.Communication/CommunicationServices/Read` plus the email permissions); **Contributor** on the ACS resource also works but is broader. #44 should try the custom role and fall back to Contributor scoped to that one resource, and record which it used. Assign it **directly** to the managed identity: assignments through an Entra group are reported not to work for managed identities.
- App settings: `ACS_ENDPOINT`, `EMAIL_SENDER` (the domain's `DoNotReply@…` address, from a Bicep output), `ALERT_EMAIL_TO` and later `DIGEST_EMAIL_TO` (deploy-time parameters, never committed).
- For the error alert (#48): a `Microsoft.Insights/actionGroups` email receiver and a `Microsoft.Insights/scheduledQueryRules` rule.

**Secrets introduced:** none, if Gary chooses ACS. If he chooses Resend, one API key in Key Vault, and #42 must come first.

## Recommendation

1. **Error alerts:** Azure Monitor log search alert + action group, in Bicep, no app code (#48 as written).
2. **Offline alert and daily digest:** `INotifier` → **ACS Email on an Azure-managed domain**, managed identity, no secrets. Accept the 30 Sep 2028 retirement, and plan the move (to Resend with Gary's own domain, or whatever is best then) well before it. *Needs Gary's confirmation.*
3. **Digest schedule:** timer at 20:00 and 21:00 UTC; only the run at 21:00 UK time sends; sent-digest record for duplicates; retry attribute plus an Error log on final failure.
4. **Templates:** a pure C# builder with `WebUtility.HtmlEncode`, inline styles and a plain-text version.

## Decisions for Gary

1. **Provider:** ACS Email (free, no secrets, no domain, retiring Sep 2028), or Resend (free, long-lived, but needs a domain you own and the Key Vault card first)?
2. **If ACS:** is a sender address like `DoNotReply@<random>.azurecomm.net` acceptable? (It can't be changed on the Azure-managed domain.)

## Suggested follow-up cards (not created)

- **[Task] Move email off Azure Communication Services before 30 September 2028**, only if Gary chooses ACS: a replacement `INotifier` (e.g. Resend with Gary's domain), Key Vault for its key, and removal of the ACS resources. Pick it up in 2027.
- **Note for #44 (no new card):** its verification should record whether the first email reached the inbox or Spam, and if needed walk Gary through a "Never send it to Spam" filter for the sender.

## Sources

- ACS service limits (email rate limits, Azure-managed vs custom domains): https://learn.microsoft.com/en-us/azure/communication-services/concepts/service-limits
- ACS Azure-managed domains (sender can't be personalised, SPF/DKIM/DMARC set up automatically, no charge for domains): https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/email/add-azure-managed-domains
- ACS email pricing: https://learn.microsoft.com/en-us/azure/communication-services/concepts/email-pricing
- ACS retirement and breaking changes (Email retiring 30 Sep 2028, maintenance mode, alternatives): https://learn.microsoft.com/en-us/azure/communication-services/acs-retirement-and-breaking-changes-guide
- ACS email with a custom role / managed identity (community answers, not official docs): https://learn.microsoft.com/en-us/answers/questions/2121165/how-to-use-custom-user-to-send-emails-from-azure-c and https://learn.microsoft.com/en-nz/answers/questions/2143376/using-managed-identities-with-azure-communication
- Azure Functions timer trigger (`WEBSITE_TIME_ZONE` not supported on Linux Flex Consumption, NCRONTAB, retries on timers): https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-timer
- Azure Functions retries: https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-error-pages
- Azure Monitor pricing (1,000 free notification emails a month): https://azure.microsoft.com/en-us/pricing/details/monitor/
- Log alert price estimate (third party): https://www.manageengine.com/it-operations-management/blog/azure-monitor-alternatives.html
- Resend pricing (free plan limits): https://resend.com/pricing ; testing domain only sends to your own address: https://resend.com/docs/knowledge-base/403-error-resend-dev-domain
- SendGrid free plan retired (third party): https://dreamlit.ai/blog/best-sendgrid-alternatives
- Brevo free plan: https://www.brevo.com/blog/free-smtp-servers/
