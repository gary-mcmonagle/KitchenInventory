// Parameters for the single (live) environment. The repo is public: nothing sensitive here.
using './main.bicep'

param location = 'uksouth'
param resourceGroupName = 'rg-cupboard-tracker'
param appName = 'cupboard'

// The Static Web App's custom domain (set up in the portal by Gary, 2026-10-18), allowed in CORS. Sign-in also needs it
// as an SPA redirect URI: deploy/entra.sh --swa-host <default> --swa-host kitchen.garymcmonagle.com.
param customDomains = ['kitchen.garymcmonagle.com']

// Push notifications (#77): the VAPID PUBLIC key (not secret; the private one is only in Key Vault). Gary
// generated the pair with deploy/vapid-keys.sh (2026-10-19).
param vapidPublicKey = 'BCgX3Wt9HcLX8zL9L2aM59V10qk5KYpypVP5Ek-hYqWiWsl4G2QU4sua8_A6UKXPMvMjjY7upoKTUk1RwEHqEzM'

// Set by deploy/infra.sh from the live apps, so re-running infra never resets the deployed commit.
param ingestCommitSha = readEnvironmentVariable('INGEST_COMMIT_SHA', 'not-deployed')
param apiCommitSha = readEnvironmentVariable('API_COMMIT_SHA', 'not-deployed')

// Open Food Facts User-Agent contact: deploy/infra.sh sets OFF_CONTACT_EMAIL from the live app (or asks), never committed.
param openFoodFactsContactEmail = readEnvironmentVariable('OFF_CONTACT_EMAIL', '')

// Offline alert recipient: deploy/infra.sh sets ALERT_EMAIL_TO from the live API app (or asks), never committed.
param alertEmailTo = readEnvironmentVariable('ALERT_EMAIL_TO', '')

// Daily digest recipient (#46): same rule, DIGEST_EMAIL_TO.
param digestEmailTo = readEnvironmentVariable('DIGEST_EMAIL_TO', '')

// Entra registrations (#36): deploy/infra.sh reads them from the 'cupboard-entra' deployment's outputs.
param entraApiClientId = readEnvironmentVariable('ENTRA_API_CLIENT_ID', '')
param entraAllowedClientIds = readEnvironmentVariable('ENTRA_ALLOWED_CLIENT_IDS', '')

// The verifier's object ID: set by deploy/infra.sh to the signed-in user unless VERIFIER_PRINCIPAL_ID is already set.
param verifierPrincipalId = readEnvironmentVariable('VERIFIER_PRINCIPAL_ID', '')

// Who can set secrets by hand in the portal (#42): set by deploy/infra.sh to the signed-in user (Gary) unless
// VAULT_OFFICER_PRINCIPAL_ID is already set.
param vaultOfficerPrincipalId = readEnvironmentVariable('VAULT_OFFICER_PRINCIPAL_ID', '')
