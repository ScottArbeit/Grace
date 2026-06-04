
# RepositoryDto

(automatically generated)

## Properties

Name | Type
------------ | -------------
`_class` | string
`repositoryId` | string
`ownerId` | string
`organizationId` | string
`repositoryName` | string
`objectStorageProvider` | [ObjectStorageProvider](ObjectStorageProvider.md)
`storageAccountName` | string
`storageContainerName` | string
`repositoryVisibility` | [RepositoryVisibility](RepositoryVisibility.md)
`repositoryStatus` | [RepositoryStatus](RepositoryStatus.md)
`branches` | Array&lt;string&gt;
`defaultServerApiVersion` | string
`defaultBranchName` | string
`saveDays` | number
`checkpointDays` | number

## Example

```typescript
import type { RepositoryDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "organizationId": e35d64a9-b990-44f5-bf02-32ad7d15630c,
  "repositoryName": null,
  "objectStorageProvider": null,
  "storageAccountName": null,
  "storageContainerName": null,
  "repositoryVisibility": null,
  "repositoryStatus": null,
  "branches": null,
  "defaultServerApiVersion": null,
  "defaultBranchName": MyBranch,
  "saveDays": null,
  "checkpointDays": null,
} satisfies RepositoryDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as RepositoryDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


