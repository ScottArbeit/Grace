
# DiffApiDto

Public diff DTO returned through diff endpoints.

## Properties

Name | Type
------------ | -------------
`_class` | string
`ownerId` | string
`organizationId` | string
`repositoryId` | string
`directoryVersionId1` | string
`directory1CreatedAt` | Date
`directoryVersionId2` | string
`directory2CreatedAt` | Date
`hasDifferences` | boolean
`differences` | [Array&lt;FileSystemDifference&gt;](FileSystemDifference.md)
`fileDiffs` | [Array&lt;FileDiff&gt;](FileDiff.md)

## Example

```typescript
import type { DiffApiDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": DiffDto,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "organizationId": e35d64a9-b990-44f5-bf02-32ad7d15630c,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "directoryVersionId1": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "directory1CreatedAt": null,
  "directoryVersionId2": 66b7b8c2-8d2f-4f04-951c-6b3486c4e5d1,
  "directory2CreatedAt": null,
  "hasDifferences": null,
  "differences": null,
  "fileDiffs": null,
} satisfies DiffApiDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as DiffApiDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


