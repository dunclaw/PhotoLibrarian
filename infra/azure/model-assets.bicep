@description('Globally unique Azure Storage account name.')
@minLength(3)
@maxLength(24)
param storageAccountName string = 'photolibrarianassets'

@description('Azure region for public model downloads.')
param location string = 'eastus2'

@description('Object ID of the GitHub Actions federated identity service principal.')
param publisherPrincipalObjectId string

@description('Subscription role definition ID from model-assets-publisher-role.bicep.')
param publisherRoleDefinitionId string

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: true
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: {
      enabled: true
      days: 30
      allowPermanentDelete: false
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 30
    }
    cors: {
      corsRules: [
        {
          allowedOrigins: [
            '*'
          ]
          allowedMethods: [
            'GET'
            'HEAD'
            'OPTIONS'
          ]
          maxAgeInSeconds: 3600
          exposedHeaders: [
            'Accept-Ranges'
            'Content-Length'
            'Content-Range'
            'ETag'
          ]
          allowedHeaders: [
            '*'
          ]
        }
      ]
    }
  }
}

resource modelAssets 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'model-assets'
  properties: {
    publicAccess: 'Blob'
  }
}

resource publisherRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: modelAssets
  name: guid(modelAssets.id, publisherPrincipalObjectId, publisherRoleDefinitionId)
  properties: {
    roleDefinitionId: publisherRoleDefinitionId
    principalId: publisherPrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

output storageAccountName string = storageAccount.name
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
output containerName string = modelAssets.name
output publicBaseUri string = '${storageAccount.properties.primaryEndpoints.blob}${modelAssets.name}/'
