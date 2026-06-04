
# GetDiffBySha256HashParameters

Parameters for retrieving a diff by SHA-256 hash.

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
`directoryVersionId1` | string
`directoryVersionId2` | string
`sha256Hash1` | string
`sha256Hash2` | string

## Example

```typescript
import type { GetDiffBySha256HashParameters } from '@grace-vcs/generated-openapi-probe'

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
  "directoryVersionId1": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "directoryVersionId2": 66b7b8c2-8d2f-4f04-951c-6b3486c4e5d1,
  "sha256Hash1": 805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243,
  "sha256Hash2": 805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243,
} satisfies GetDiffBySha256HashParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as GetDiffBySha256HashParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


