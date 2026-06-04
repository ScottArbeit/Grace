
# BranchDto

(automatically generated)

## Properties

Name | Type
------------ | -------------
`_class` | string
`branchId` | string
`branchName` | string
`parentBranchId` | string
`basedOn` | string
`repositoryId` | string
`userId` | string
`promotionEnabled` | boolean
`commitEnabled` | boolean
`checkpointEnabled` | boolean
`saveEnabled` | boolean
`tagEnabled` | boolean
`latestPromotion` | string
`latestCommit` | string
`latestCheckpoint` | string
`latestSave` | string
`createdAt` | Date
`updatedAt` | [BranchDtoUpdatedAt](BranchDtoUpdatedAt.md)
`deletedAt` | [BranchDtoUpdatedAt](BranchDtoUpdatedAt.md)

## Example

```typescript
import type { BranchDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "branchId": de7bf47d-23ae-4599-af68-68a317ea390d,
  "branchName": MyBranch,
  "parentBranchId": de7bf47d-23ae-4599-af68-68a317ea390d,
  "basedOn": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "userId": null,
  "promotionEnabled": null,
  "commitEnabled": null,
  "checkpointEnabled": null,
  "saveEnabled": null,
  "tagEnabled": null,
  "latestPromotion": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "latestCommit": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "latestCheckpoint": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "latestSave": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "createdAt": null,
  "updatedAt": null,
  "deletedAt": null,
} satisfies BranchDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as BranchDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


