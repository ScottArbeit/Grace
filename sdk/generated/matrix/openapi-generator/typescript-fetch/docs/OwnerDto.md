
# OwnerDto

(automatically generated)

## Properties

Name | Type
------------ | -------------
`_class` | string
`ownerId` | string
`ownerName` | string
`ownerType` | [OwnerType](OwnerType.md)
`description` | string
`searchVisibility` | [SearchVisibility](SearchVisibility.md)
`createdAt` | Date
`updatedAt` | [BranchDtoUpdatedAt](BranchDtoUpdatedAt.md)
`deletedAt` | [BranchDtoUpdatedAt](BranchDtoUpdatedAt.md)
`deleteReason` | string

## Example

```typescript
import type { OwnerDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "ownerName": null,
  "ownerType": null,
  "description": null,
  "searchVisibility": null,
  "createdAt": null,
  "updatedAt": null,
  "deletedAt": null,
  "deleteReason": null,
} satisfies OwnerDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as OwnerDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


