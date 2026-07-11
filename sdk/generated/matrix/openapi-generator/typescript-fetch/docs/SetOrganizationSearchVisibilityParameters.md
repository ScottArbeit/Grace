
# SetOrganizationSearchVisibilityParameters

Parameters for the /organization/setSearchVisibility endpoint.

## Properties

Name | Type
------------ | -------------
`correlationId` | string
`principal` | string
`properties` | { [key: string]: string; }
`ownerId` | string
`ownerName` | string
`organizationId` | string
`organizationName` | string
`searchVisibility` | [SearchVisibility](SearchVisibility.md)

## Example

```typescript
import type { SetOrganizationSearchVisibilityParameters } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "correlationId": null,
  "principal": null,
  "properties": null,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "ownerName": null,
  "organizationId": e35d64a9-b990-44f5-bf02-32ad7d15630c,
  "organizationName": null,
  "searchVisibility": null,
} satisfies SetOrganizationSearchVisibilityParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SetOrganizationSearchVisibilityParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


