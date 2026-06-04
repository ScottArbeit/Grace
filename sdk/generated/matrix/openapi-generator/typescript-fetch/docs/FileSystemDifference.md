
# FileSystemDifference


## Properties

Name | Type
------------ | -------------
`differenceType` | any
`fileSystemEntryType` | any
`relativePath` | any

## Example

```typescript
import type { FileSystemDifference } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "differenceType": null,
  "fileSystemEntryType": null,
  "relativePath": null,
} satisfies FileSystemDifference

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as FileSystemDifference
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


