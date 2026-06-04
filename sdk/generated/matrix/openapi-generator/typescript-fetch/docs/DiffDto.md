
# DiffDto

(automatically generated)

## Properties

Name | Type
------------ | -------------
`_class` | string
`hasDifferences` | boolean
`directoryId1` | string
`directory1CreatedAt` | Date
`directoryId2` | string
`directory2CreatedAt` | Date
`differences` | [Array&lt;FileSystemDifference&gt;](FileSystemDifference.md)
`fileDiffs` | [Array&lt;FileDiff&gt;](FileDiff.md)

## Example

```typescript
import type { DiffDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "hasDifferences": null,
  "directoryId1": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "directory1CreatedAt": null,
  "directoryId2": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "directory2CreatedAt": null,
  "differences": null,
  "fileDiffs": null,
} satisfies DiffDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as DiffDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


