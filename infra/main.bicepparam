// Parameters for the single (live) environment. The repo is public: nothing sensitive here.
using './main.bicep'

param location = 'uksouth'
param resourceGroupName = 'rg-cupboard-tracker'
param appName = 'cupboard'

// Set by deploy/infra.sh from the live apps, so re-running infra never resets the deployed commit.
param ingestCommitSha = readEnvironmentVariable('INGEST_COMMIT_SHA', 'not-deployed')
param apiCommitSha = readEnvironmentVariable('API_COMMIT_SHA', 'not-deployed')

// The verifier's object ID: set by deploy/infra.sh to the signed-in user unless VERIFIER_PRINCIPAL_ID is already set.
param verifierPrincipalId = readEnvironmentVariable('VERIFIER_PRINCIPAL_ID', '')
