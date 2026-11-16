targetScope = 'subscription'

@description('Dedicated resource group containing only model asset hosting resources.')
param resourceGroupName string

@description('Email address for cost threshold notifications.')
param alertEmail string

@description('Monthly budget in USD for the model asset resource group.')
@minValue(1)
param monthlyBudgetUsd int = 25

@description('First day of the current month in UTC.')
param startDate string = utcNow('yyyy-MM-01')

resource modelAssetsBudget 'Microsoft.Consumption/budgets@2023-05-01' = {
  name: 'photolibrarian-model-assets-monthly'
  properties: {
    category: 'Cost'
    amount: monthlyBudgetUsd
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: '${startDate}T00:00:00Z'
      endDate: '2036-01-01T00:00:00Z'
    }
    filter: {
      dimensions: {
        name: 'ResourceGroupName'
        operator: 'In'
        values: [
          resourceGroupName
        ]
      }
    }
    notifications: {
      actual50: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 50
        contactEmails: [
          alertEmail
        ]
      }
      actual80: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        contactEmails: [
          alertEmail
        ]
      }
      actual100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        contactEmails: [
          alertEmail
        ]
      }
      forecast100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        contactEmails: [
          alertEmail
        ]
      }
    }
  }
}
