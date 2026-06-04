
# DirectoryVersion


## Properties

Name | Type
------------ | -------------
`_class` | string
`directories` | Array&lt;any&gt;
`files` | Array&lt;any&gt;
`size` | number
`recursiveSize` | number
`createdAt` | Date

## Example

```typescript
import type { DirectoryVersion } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "directories": null,
  "files": null,
  "size": null,
  "recursiveSize": null,
  "createdAt": null,
} satisfies DirectoryVersion

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as DirectoryVersion
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


