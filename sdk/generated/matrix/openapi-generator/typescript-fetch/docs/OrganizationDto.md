
# OrganizationDto

(automatically generated)

## Properties

Name | Type
------------ | -------------
`_class` | string
`organizationId` | string
`organizationName` | string
`ownerId` | string
`organizationType` | [OrganizationType](OrganizationType.md)
`description` | string
`searchVisibility` | [SearchVisibility](SearchVisibility.md)
`createdAt` | Date
`updatedAt` | [BranchDtoUpdatedAt](BranchDtoUpdatedAt.md)
`deletedAt` | [BranchDtoUpdatedAt](BranchDtoUpdatedAt.md)
`deleteReason` | string

## Example

```typescript
import type { OrganizationDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "organizationId": e35d64a9-b990-44f5-bf02-32ad7d15630c,
  "organizationName": null,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "organizationType": null,
  "description": null,
  "searchVisibility": null,
  "createdAt": null,
  "updatedAt": null,
  "deletedAt": null,
  "deleteReason": null,
} satisfies OrganizationDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as OrganizationDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


