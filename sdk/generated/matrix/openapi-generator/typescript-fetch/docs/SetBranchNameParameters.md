
# SetBranchNameParameters

Parameters for the /branch/setName endpoint.

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
`repositoryId` | string
`repositoryName` | string
`branchId` | string
`branchName` | string
`newName` | string

## Example

```typescript
import type { SetBranchNameParameters } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "correlationId": null,
  "principal": null,
  "properties": null,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "ownerName": null,
  "organizationId": e35d64a9-b990-44f5-bf02-32ad7d15630c,
  "organizationName": null,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "repositoryName": null,
  "branchId": de7bf47d-23ae-4599-af68-68a317ea390d,
  "branchName": MyBranch,
  "newName": ReleaseCandidate,
} satisfies SetBranchNameParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SetBranchNameParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


