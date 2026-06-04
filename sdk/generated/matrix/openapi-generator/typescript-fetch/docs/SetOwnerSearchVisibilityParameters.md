
# SetOwnerSearchVisibilityParameters

Parameters for the /owner/setSearchVisibility endpoint.

## Properties

Name | Type
------------ | -------------
`correlationId` | string
`principal` | string
`ownerId` | string
`ownerName` | string
`searchVisibility` | [SearchVisibility](SearchVisibility.md)

## Example

```typescript
import type { SetOwnerSearchVisibilityParameters } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "correlationId": null,
  "principal": null,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "ownerName": null,
  "searchVisibility": null,
} satisfies SetOwnerSearchVisibilityParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SetOwnerSearchVisibilityParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


