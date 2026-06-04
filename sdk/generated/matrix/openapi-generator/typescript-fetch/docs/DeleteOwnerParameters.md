
# DeleteOwnerParameters

Parameters for the /owner/delete endpoint.

## Properties

Name | Type
------------ | -------------
`correlationId` | string
`principal` | string
`ownerId` | string
`ownerName` | string
`force` | boolean
`deleteReason` | string

## Example

```typescript
import type { DeleteOwnerParameters } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "correlationId": null,
  "principal": null,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "ownerName": null,
  "force": false,
  "deleteReason": Owner retired.,
} satisfies DeleteOwnerParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as DeleteOwnerParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


