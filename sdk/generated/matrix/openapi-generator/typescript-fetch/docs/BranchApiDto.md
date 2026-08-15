
# BranchApiDto

Public branch DTO returned through branch endpoints.

## Properties

Name | Type
------------ | -------------
`_class` | string
`branchId` | string
`branchName` | string
`parentBranchId` | string
`ownerId` | string
`organizationId` | string
`repositoryId` | string
`basedOn` | [ReferenceApiDto](ReferenceApiDto.md)
`userId` | string
`assignEnabled` | boolean
`promotionEnabled` | boolean
`commitEnabled` | boolean
`checkpointEnabled` | boolean
`saveEnabled` | boolean
`tagEnabled` | boolean
`externalEnabled` | boolean
`autoRebaseEnabled` | boolean
`promotionMode` | string
`latestReference` | [ReferenceApiDto](ReferenceApiDto.md)
`latestPromotion` | [TypedReferenceApiDto](TypedReferenceApiDto.md)
`latestCommit` | [TypedReferenceApiDto](TypedReferenceApiDto.md)
`latestCheckpoint` | [TypedReferenceApiDto](TypedReferenceApiDto.md)
`latestSave` | [TypedReferenceApiDto](TypedReferenceApiDto.md)
`shouldRecomputeLatestReferences` | boolean
`createdAt` | Date
`updatedAt` | Date
`deletedAt` | Date
`deleteReason` | string

## Example

```typescript
import type { BranchApiDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": BranchDto,
  "branchId": de7bf47d-23ae-4599-af68-68a317ea390d,
  "branchName": MyBranch,
  "parentBranchId": de7bf47d-23ae-4599-af68-68a317ea390d,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "organizationId": e35d64a9-b990-44f5-bf02-32ad7d15630c,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "basedOn": null,
  "userId": null,
  "assignEnabled": null,
  "promotionEnabled": null,
  "commitEnabled": null,
  "checkpointEnabled": null,
  "saveEnabled": null,
  "tagEnabled": null,
  "externalEnabled": null,
  "autoRebaseEnabled": null,
  "promotionMode": IndividualOnly,
  "latestReference": null,
  "latestPromotion": null,
  "latestCommit": null,
  "latestCheckpoint": null,
  "latestSave": null,
  "shouldRecomputeLatestReferences": null,
  "createdAt": null,
  "updatedAt": null,
  "deletedAt": null,
  "deleteReason": null,
} satisfies BranchApiDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as BranchApiDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


