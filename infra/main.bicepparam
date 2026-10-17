// Parameters for the single (live) environment. The repo is public: nothing sensitive here.
using './main.bicep'

param location = 'uksouth'
param resourceGroupName = 'rg-cupboard-tracker'
param appName = 'cupboard'

// Set by deploy/infra.sh from the live apps, so re-running infra never resets the deployed commit.
param ingestCommitSha = readEnvironmentVariable('INGEST_COMMIT_SHA', 'not-deployed')
param apiCommitSha = readEnvironmentVariable('API_COMMIT_SHA', 'not-deployed')

// Open Food Facts User-Agent contact: deploy/infra.sh sets OFF_CONTACT_EMAIL from the live app (or asks), never committed.
param openFoodFactsContactEmail = readEnvironmentVariable('OFF_CONTACT_EMAIL', '')

// Offline alert recipient: deploy/infra.sh sets ALERT_EMAIL_TO from the live API app (or asks), never committed.
param alertEmailTo = readEnvironmentVariable('ALERT_EMAIL_TO', '')

// Daily digest recipient (#46): same rule, DIGEST_EMAIL_TO.
param digestEmailTo = readEnvironmentVariable('DIGEST_EMAIL_TO', '')

// The verifier's object ID: set by deploy/infra.sh to the signed-in user unless VERIFIER_PRINCIPAL_ID is already set.
param verifierPrincipalId = readEnvironmentVariable('VERIFIER_PRINCIPAL_ID', '')
