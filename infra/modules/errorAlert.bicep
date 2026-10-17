// "Cupboard Tracker errors" (#48, spike #39): Azure Monitor emails the alert address when either Function app has an
// exception, an Error (or worse) trace, or a 5xx request. No app code is involved, so it still works when the app is
// the thing that's broken. 4xx validation failures and warnings don't count.
//
// Noise control: the rule is stateless (no auto-resolve, so no "resolved" emails) and mutes its actions for an hour
// after firing, so a continuing problem emails at most once an hour.

@description('Azure region of the Log Analytics workspace.')
param location string

@description('Resource tags.')
param tags object

@description('Log Analytics workspace behind Application Insights (in this resource group).')
param workspaceName string

@description('Who gets the error email (the offline alert address). Never committed.')
@secure()
param alertEmailTo string

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-cupboard-alerts'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'cupboard'
    enabled: true
    emailReceivers: [
      {
        name: 'alert-email'
        emailAddress: alertEmailTo
        useCommonAlertSchema: true
      }
    ]
  }
}

var errorsQuery = '''
union
  (AppExceptions | project TimeGenerated, AppRoleName, Kind = "exception", Detail = strcat(ExceptionType, ": ", OuterMessage)),
  (AppTraces | where SeverityLevel >= 3 | project TimeGenerated, AppRoleName, Kind = "error log", Detail = Message),
  (AppRequests | where Success == false and toint(ResultCode) >= 500 | project TimeGenerated, AppRoleName, Kind = "failed request", Detail = strcat(Name, " ", ResultCode))
'''

resource errorRule 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-cupboard-errors'
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    displayName: 'Cupboard Tracker errors'
    description: 'An exception, an Error log or a 5xx request in a Cupboard Tracker Function app. See what happened with: deploy/verify/logs.sh ingest 30 (or api). Test it with POST /api/diagnostics/test-error.'
    severity: 1
    enabled: true
    scopes: [
      workspace.id
    ]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: errorsQuery
          timeAggregation: 'Count'
          operator: 'GreaterThanOrEqual'
          threshold: 1
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: false
    muteActionsDuration: 'PT1H'
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}

output ruleName string = errorRule.name
