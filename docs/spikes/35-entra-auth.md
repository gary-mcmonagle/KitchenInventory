# Spike #35: Entra ID sign-in for the React app and API Function

Card: https://trello.com/c/lFc5tXyq

## Question

What is the simplest robust way for the React app (Azure Static Web Apps, **free tier**) to sign household users in with **Entra ID** and call the API Function app with a **bearer token**?

## Summary

- **No linked backend on the Free plan** (confirmed). The React app calls the API Function app's own hostname directly, and the Function app allows the Static Web App's origin through CORS.
- **Sign-in with MSAL React** (authorization code + PKCE), not Static Web Apps' built-in auth. The built-in Entra provider on the Free plan lets *any* Microsoft account sign in and gives the browser no access token for our API.
- **Validate the token in our own code** in the API Function app (a small worker middleware using Microsoft's `Microsoft.IdentityModel` libraries), not App Service built-in auth ("Easy Auth"). Easy Auth's support on Flex Consumption is unclear, and code validation behaves the same locally and in Azure and is unit-testable.
- **Two single-tenant app registrations** in Gary's own Entra tenant: an **API** registration exposing `access_as_user` that pre-authorises the **SPA** registration, which has SPA redirect URIs for localhost and the Static Web App.
- **Only invited people can sign in:** the registrations are single-tenant, household members are **invited as guests**, and the API also checks the tenant and calling app.
- **Registrations as code** with the **Microsoft Graph Bicep extension** (GA since August 2025), deployed by `deploy/infra.sh`.
- **Everything here is free.**

## 1. Linked backends on the Free plan

Not available. The Static Web Apps plan comparison lists "APIs via Azure Functions" as **Managed** only on Free, and **"Managed or Bring your own Functions app"** on Standard. Managed functions are HTTP-only, and our API app also hosts the change-feed processor and timers, so it must be a separate Function app.

**So:** the React app calls `https://<api-app>.azurewebsites.net/api/...` directly, with `Authorization: Bearer <token>`.

**CORS** is set on the Function app (platform CORS, `siteConfig.cors.allowedOrigins` in Bicep) to the Static Web App's origin, plus any custom domain later. No `supportCredentials`: the token travels in a header, not a cookie. Localhost isn't needed in Azure, because local development runs the API locally too (see [Local development](#local-development)).

## 2. Sign-in in the SPA: MSAL React

| | Static Web Apps built-in auth (Free) | **MSAL React** |
|---|---|---|
| Restrict to our tenant | No. "The preconfigured Microsoft Entra ID provider allows any Microsoft account to sign in." Restricting it needs a custom provider, which is **Standard plan only**. | Yes: single-tenant authority |
| Access token for our API | No. The built-in auth gives a session cookie and `/.auth/me` (a client principal), not a token for another API. | Yes: `acquireTokenSilent` with our API's scope |
| Works on the Free plan | Yes | Yes (it's just JavaScript) |

**Recommendation: `@azure/msal-browser` + `@azure/msal-react`**, authorization code flow with PKCE (MSAL's default for SPAs).

- **Authority:** `https://login.microsoftonline.com/<tenantId>` (single tenant).
- **Scope:** `api://<tenantId>/cupboard-tracker-api/access_as_user`.
- **Redirect flow** (`loginRedirect`, `acquireTokenRedirect`), not pop-ups: it's phone-first, and pop-ups are awkward on mobile browsers.
- **Token calls:** `acquireTokenSilent` before each API call (MSAL caches and refreshes). On `InteractionRequiredAuthError`, fall back to `acquireTokenRedirect`.
- **Cache:** `localStorage`, so reopening the app on the phone doesn't mean signing in again. The trade-off is that a cross-site scripting bug could read tokens. Keep a strict Content Security Policy in `staticwebapp.config.json`, and never render HTML from untrusted data (Open Food Facts names).
- **Expect a re-sign-in roughly daily.** SPA refresh tokens last **24 hours, fixed** ("a non-adjustable, non-sliding window"). After that, MSAL tries a hidden iframe, which usually fails on phones because third-party cookies are blocked, so it redirects. If Gary ticked "Stay signed in", the redirect normally passes straight through without a password. That's acceptable for a household app.
- **Config is not secret:** client ID, tenant ID, API scope and API base URL are build-time values (`VITE_…`), filled in by the UI deploy script from Bicep outputs.

## 3. Token validation in the API: in code, not Easy Auth

| | Easy Auth on the Function app | **Validate in code (worker middleware)** |
|---|---|---|
| Works on Flex Consumption | **Unclear.** A Microsoft Q&A answer in July 2024 said, per the product team, it wasn't enabled on Flex Consumption. A 2025 Q&A post shows it working after excluding `/admin/*`. The current Flex Consumption docs don't mention it either way. | Yes. It's our code. |
| Local development | Doesn't run under `func start`, so local behaviour differs (no auth, or faked headers) | Identical locally and in Azure |
| Unit testing | Only the header-reading part | The validator and the middleware are both testable |
| Per-route exceptions | `excludedPaths` in config (needed for function-key routes such as #48's `/api/diagnostics/test-error` and the `/admin` endpoints) | Explicit in code: only functions that need a user are checked |
| Code to write | Almost none | A small middleware (~100 lines) |

**Recommendation: validate the token in code.**

- **Where:** an `IFunctionsWorkerMiddleware` in the API Function app (the host, which is the adapter layer). It runs for HTTP-triggered functions that need a user, and lets the others (function-key or timer functions) through. Those HTTP functions use `AuthorizationLevel.Anonymous`, because the middleware does the auth.
- **How:** `Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler`, with signing keys from the tenant's OpenID configuration (`https://login.microsoftonline.com/<tenantId>/v2.0/.well-known/openid-configuration`), fetched and cached by `ConfigurationManager<OpenIdConnectConfiguration>`. These are Microsoft's own libraries, the same ones Microsoft.Identity.Web uses underneath.
- **Checks:**
  - signature, `exp` / `nbf`;
  - `iss` = `https://login.microsoftonline.com/<tenantId>/v2.0` (v2 tokens: the API registration sets `requestedAccessTokenVersion: 2`);
  - `aud` = the API registration's client ID;
  - `tid` = our tenant;
  - `azp` = the SPA registration's client ID (only our app can call);
  - `scp` contains `access_as_user`.
  - Missing or invalid token → **401**. Valid token with the wrong `azp` or `scp` → **403**.
- **What Application sees:** an `ICurrentUser` (or a `CurrentUser` record passed in) with `ObjectId` (`oid`, used for `namedBy` / `checkedOutBy`) and `DisplayName` (`name`). No JWT types reach Application or Domain.
- The **ingest** Function app keeps function-key auth and isn't affected.
- The SignalR negotiate endpoint (spike #38) uses the same middleware.

## 4. App registrations

Both are **single-tenant** (`signInAudience: AzureADMyOrg`) in Gary's tenant.

**API: `cupboard-tracker-api`**
- Application ID URI: `api://<tenantId>/cupboard-tracker-api`. This format is accepted without a verified domain, and it avoids the chicken-and-egg problem of `api://<own appId>` in Bicep.
- Exposes one delegated scope, **`access_as_user`** ("Access Cupboard Tracker as you"). Its scope ID is a fixed GUID from `guid()` in Bicep.
- **`preAuthorizedApplications`**: the SPA's client ID for that scope, so users never see a consent prompt.
- `requestedAccessTokenVersion: 2`.
- No secret, no redirect URIs.

**SPA: `cupboard-tracker-spa`**
- Platform **Single-page application**, with redirect URIs:
  - `http://localhost:5173/` (Vite's default dev port)
  - `https://<static-web-app-default-hostname>/`
  - any custom domain later.
- No secret, no implicit grant.
- It doesn't need `requiredResourceAccess` for the API: MSAL asks for the scope at runtime, and pre-authorisation covers consent. Leaving it out also avoids a circular dependency (the API references the SPA, but not the other way round).

Each gets a **service principal** (enterprise app).

## 5. Restricting access

- **Tenant:** Gary's existing Entra tenant (the directory behind his Azure subscription). No separate or "external" tenant: an External ID tenant is meant for consumer sign-up and is more than we need.
- **Who can sign in:** single-tenant means only **users in that tenant**: Gary, plus household members Gary **invites as guests** (Entra admin center → Users → Invite external user). Guests sign in with their own Microsoft account, or with a one-time passcode emailed to them if they don't have one. **Removing someone = deleting the guest.**
- **Belt and braces:** turn on **"Assignment required"** on the SPA's enterprise app (`appRoleAssignmentRequired: true` on its service principal) and assign each household user **directly**. Assigning a **group** needs Entra ID P1, so use direct assignment. *Check during the registrations task that assignment works on the free tier; if it doesn't, the tenant boundary above is still the control.*
- **In the API:** the `tid` and `azp` checks mean a token for some other app, or from another tenant, is rejected even if it's otherwise valid.
- **Decision (Gary):** use his existing tenant.

## 6. Registrations as code: Microsoft Graph Bicep extension

- **Recommended:** the **Microsoft Graph Bicep extension** (`extension microsoftGraphV1` in Bicep, configured in `bicepconfig.json`). It's GA (Microsoft identity blog, August 2025) and supports `Microsoft.Graph/applications` and `Microsoft.Graph/servicePrincipals`. Each app needs a stable `uniqueName`, so redeploys update rather than duplicate.
- Put it in `infra/entra.bicep`, deployed **on its own** by `deploy/entra.sh` as the dedicated deployer account (see [Deployment identity](#deployment-identity-least-privilege)). It outputs the client IDs, tenant ID and scope, which the infra, Function and UI deploys take as plain parameters (they aren't secrets).
- **Order:** the Static Web App before the SPA registration (it needs the hostname); the SPA registration before the API registration (pre-authorisation needs the SPA's client ID).
- **Gary's admin account can't run it.** Microsoft's Graph Bicep reference says "Permissions for personal Microsoft accounts cannot be used to deploy Microsoft Graph resources declared in Bicep files", and Gary's account is Gmail-based. That applies to reading existing Graph resources too, so the main infra deployment can't look the registrations up either; it gets their IDs as parameters.
- Assigning users to the app ("Assignment required") stays a manual step, done in the Entra admin center when someone is invited.

### Deployment identity (least privilege)

Gary asked for an account limited to exactly what's needed. The design:

**A dedicated cloud-only user**, e.g. `cupboard-deployer@<tenant>.onmicrosoft.com`, created once by Gary in the Entra admin center.
- It's a member user of the tenant, so Graph Bicep accepts it. No licence or mailbox.
- **MFA on** (Microsoft Authenticator). Gary signs in as it only when the registrations change, which is rare: first set-up and when a redirect URI changes.
- It never uses the app, and it isn't assigned to it.

**One Entra role: Application Developer.** Nothing else.
- What it can do (Microsoft's permissions reference): create app registrations and service principals, becoming their **owner**, and update only the apps it owns. It can also grant OAuth consent on its own behalf, which we don't use.
- What it can't do: manage users or guests, groups, other people's apps, tenant settings or role assignments.
- Microsoft marks it "privileged", because an owner can add credentials to the apps it owns. It's still the smallest built-in role that can create app registrations; Application Administrator and Global Administrator are far broader.
- Because it can only edit apps it owns, it must **create** the registrations (rather than take over ones made by hand). It sets "Assignment required" on the SPA's service principal in Bicep, which it can do as owner.

**Azure access: only what an ARM deployment needs.** The Graph extension runs inside an ARM deployment, which needs a scope to deploy to, but `entra.bicep` contains no Azure resources.
- A **custom role** ("Cupboard Tracker deployment runner") assigned on the project's resource group, with only:
  - `Microsoft.Resources/deployments/*`
  - `Microsoft.Resources/subscriptions/resourceGroups/read`
- It can run and read deployments in that one resource group, and can't create, change or delete any Azure resource.
- The registrations task must confirm this is enough. If ARM asks for more, the fallback is to drop ARM for this step and use an idempotent `az ad app` / `az ad sp` script run as the same user, which needs **no Azure role at all**.

**What stays with Gary's own admin account** (in the portal, by hand): creating the deployer user and giving it its role (once), inviting guests, and assigning users to the app.

**Recommended hardening:** set the tenant's "Users can register applications" to **No**. Only the deployer (through its role) and Gary can then create apps. Guests can't by default anyway.

**Running it from the Mac:** use a separate Azure CLI profile, so the deployer's sign-in never mixes with Gary's:

```bash
AZURE_CONFIG_DIR=~/.azure-cupboard-deployer az login --tenant <tenantId>   # interactive, with MFA
AZURE_CONFIG_DIR=~/.azure-cupboard-deployer ./deploy/entra.sh
```

**Deploy order:** `deploy/infra.sh` (Gary: Static Web App and the rest) → `deploy/entra.sh` (deployer: the registrations, taking the Static Web App hostname as input) → `deploy/infra.sh` / `deploy/functions.sh` / `deploy/ui.sh` with the client IDs as parameters. After the first set-up, `entra.sh` only runs when the registrations change.

## 7. Cost

| Item | Cost |
|---|---|
| Static Web Apps Free plan | Free ("For personal projects") |
| Entra ID (Free), app registrations, direct user assignment | Free |
| Guest users | Free up to 50,000 monthly active users |
| MSAL, `Microsoft.IdentityModel` | Free (open source) |
| Graph Bicep extension | Free |

Nothing here needs a paid tier. Group-based app assignment (P1) and custom auth providers on Static Web Apps (Standard) are avoided on purpose.

## Recommended design

```mermaid
sequenceDiagram
    autonumber
    actor U as Household user (phone)
    participant SPA as React app (Static Web Apps, Free)
    participant Entra as Entra ID (Gary's tenant)
    participant API as API Function app
    participant MW as Auth middleware
    U->>SPA: Open app
    SPA->>Entra: loginRedirect (auth code + PKCE, single tenant)
    Entra->>U: Sign in (Gary, or an invited guest)
    Entra-->>SPA: Redirect with code
    SPA->>Entra: Redeem code + PKCE verifier
    Entra-->>SPA: ID token, access token (aud = API, scp = access_as_user), 24h refresh token
    SPA->>API: GET /api/stock, Authorization: Bearer <access token> (CORS: SWA origin allowed)
    API->>MW: HTTP-triggered function invoked
    MW->>MW: Validate signature (cached tenant keys), iss, aud, exp, tid, azp = SPA, scp
    alt valid
        MW->>API: CurrentUser(oid, name)
        API-->>SPA: 200 + JSON
    else invalid or missing
        MW-->>SPA: 401 (or 403 for wrong azp/scp)
        SPA->>Entra: acquireTokenSilent / acquireTokenRedirect
    end
```

## Local development

- **UI:** `npm run dev` on `http://localhost:5173`. Sign in for real against Entra (the localhost redirect URI is registered). A `.env.local` (git-ignored) holds the non-secret client ID, tenant ID and scope, plus `VITE_API_BASE_URL=http://localhost:7071`.
- **API:** `func start` on `http://localhost:7071`. The middleware validates the same real tokens as in Azure. `local.settings.json` (git-ignored) holds the tenant ID and the two client IDs, plus `Host.CORS = http://localhost:5173`.
- No auth bypass in the API: local behaves like production.
- **Unit tests** need no network or Entra:
  - **Validator:** generate an RSA key in the test, sign tokens with it, and feed the validator a static signing-key configuration. Cover a good token, expired, wrong `aud` / `iss` / `tid` / `azp`, missing `scp`, and a bad signature.
  - **Middleware:** a fake validator. Cover 401 without a header, 403 on scope, and `CurrentUser` set on success. Functions without auth are left alone.
  - **Use cases:** take a fake `CurrentUser`.

## Recommendation

1. **UI:** MSAL React, auth code + PKCE, redirect flow, single-tenant authority, `localStorage` cache, `acquireTokenSilent` → redirect fallback. Call the API Function app directly; CORS allows the Static Web App origin.
2. **API:** validate bearer tokens in a worker middleware (`Microsoft.IdentityModel`), checking signature, issuer, audience, tenant, `azp` and `scp`. Expose `CurrentUser` to Application. Don't use Easy Auth.
3. **Registrations:** single-tenant API (`access_as_user`, pre-authorising the SPA, v2 tokens) and SPA (SPA redirect URIs), as code with the Graph Bicep extension in `infra/entra.bicep`. Deployed by a dedicated cloud-only user with only the **Application Developer** role, plus a deployments-only custom role on the resource group.
4. **Access:** Gary's tenant only; invite household members as guests; "Assignment required" with direct user assignment if available on the free tier.
5. **Cost:** free.

## Decisions (Gary, 2026-10-17)

1. **Tenant:** use Gary's existing tenant.
2. **Admin account:** Gary's is a personal (Gmail-based) Microsoft account, so the registrations are deployed by a dedicated account **limited to exactly what's needed**: see [Deployment identity](#deployment-identity-least-privilege).
3. **Re-sign-in:** roughly daily is fine.

## Suggested follow-up cards (not created)

- **Notes for [Task] Entra ID app registrations** (no new card): build the two registrations above in `infra/entra.bicep` with the Graph extension, in the order given. Includes Gary's one-off manual steps (create the deployer user with MFA, give it Application Developer, assign the custom deployment role), `deploy/entra.sh`, and confirming the custom role is enough (fallback: an `az ad` script). Test "Assignment required" on the free tier.
- **Notes for [Task] React UI skeleton** (no new card): MSAL React set up as above, the `VITE_…` config from Bicep outputs, and a strict Content Security Policy in `staticwebapp.config.json`.
- **[Task] API bearer-token validation middleware:** the middleware, validator, `CurrentUser`, CORS in Bicep, and the tests listed above. It could be part of the first API feature ([Feature] Stock list on the phone) instead of a card of its own.

## Sources

- Static Web Apps plans (Free: managed functions only; custom auth providers Standard only): https://learn.microsoft.com/en-us/azure/static-web-apps/plans
- Static Web Apps authentication (preconfigured Entra provider allows any Microsoft account; restricting needs a custom provider): https://learn.microsoft.com/en-us/azure/static-web-apps/authentication-authorization
- Flex Consumption plan (considerations; no mention of built-in auth): https://learn.microsoft.com/en-us/azure/azure-functions/flex-consumption-plan
- Easy Auth on Flex Consumption: July 2024 Q&A (not enabled): https://learn.microsoft.com/en-us/answers/questions/2140342/azure-functions-flex-consumption-plan-authenticati ; 2025 Q&A (working with `/admin/*` excluded): https://learn.microsoft.com/en-us/answers/a/1897287
- App Service / Functions Entra auth (allowed audiences, `allowedApplications`, `x-ms-client-principal`, 401 for APIs): https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-provider-aad
- MSAL.js token lifetimes (SPA refresh tokens 24 hours, non-sliding; iframe renewal fails with third-party cookies blocked): https://learn.microsoft.com/en-us/entra/msal/javascript/browser/token-lifetimes
- Refresh token lifetimes: https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens
- Group-based app assignment needs P1/P2: https://learn.microsoft.com/en-us/entra/identity/users/groups-saasapps
- External ID pricing (guests free up to 50,000 MAU): https://learn.microsoft.com/en-us/entra/external-id/external-identities-pricing
- Application Developer role (create apps as owner, update owned apps only): https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/permissions-reference#application-developer
- Graph Bicep applications reference (personal Microsoft accounts can't deploy Graph resources): https://learn.microsoft.com/en-us/graph/templates/reference/applications?view=graph-bicep-1.0
- Graph Bicep templates GA: https://devblogs.microsoft.com/identity/bicep-templates-for-microsoft-entra-id-resources-is-ga/ ; reference (e.g. oauth2PermissionGrants, account-type support): https://learn.microsoft.com/en-us/graph/templates/reference/oauth2permissiongrants?view=graph-bicep-1.0
