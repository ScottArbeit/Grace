
# ResolveReferenceEventBoundaryParameters

Supplies the exact materialized local root whose missing Watch cursor must match or baseline.

## Properties

Name | Type
------------ | -------------
`correlationId` | string
`principal` | string
`ownerId` | string
`ownerName` | string
`organizationId` | string
`organizationName` | string
`repositoryId` | string
`repositoryName` | string
`branchId` | string
`branchName` | string
`directoryVersionId` | string
`sha256Hash` | string
`blake3Hash` | string

## Example

```typescript
import type { ResolveReferenceEventBoundaryParameters } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "correlationId": null,
  "principal": null,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "ownerName": null,
  "organizationId": e35d64a9-b990-44f5-bf02-32ad7d15630c,
  "organizationName": null,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "repositoryName": null,
  "branchId": de7bf47d-23ae-4599-af68-68a317ea390d,
  "branchName": MyBranch,
  "directoryVersionId": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "sha256Hash": 805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243,
  "blake3Hash": 9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d,
} satisfies ResolveReferenceEventBoundaryParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ResolveReferenceEventBoundaryParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


