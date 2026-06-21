
# FileManifest

Server-accepted reconstruction contract for one manifest-backed file.

## Properties

Name | Type
------------ | -------------
`_class` | string
`manifestAddress` | string
`chunkingSuiteId` | string
`fileContentHash` | string
`storagePoolId` | string
`size` | number
`blocks` | [Array&lt;ContentBlock&gt;](ContentBlock.md)

## Example

```typescript
import type { FileManifest } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "manifestAddress": null,
  "chunkingSuiteId": rabin-v1,
  "fileContentHash": null,
  "storagePoolId": null,
  "size": null,
  "blocks": null,
} satisfies FileManifest

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as FileManifest
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


