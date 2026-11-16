targetScope = 'subscription'

resource modelAssetPublisherRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, 'PhotoLibrarianModelAssetPublisher')
  properties: {
    roleName: 'PhotoLibrarian Model Asset Publisher'
    description: 'Read and create model asset blobs without blob deletion permission.'
    type: 'CustomRole'
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
        ]
        notDataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
        ]
      }
    ]
    assignableScopes: [
      subscription().id
    ]
  }
}

output roleDefinitionId string = modelAssetPublisherRole.id
