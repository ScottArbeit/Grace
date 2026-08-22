
# PrepareDirectoryVersionZipParameters


## Properties

Name | Type
------------ | -------------
`correlationId` | string
`principal` | string
`repositoryId` | string
`directoryVersionId` | string
`cachePublicKey` | [CachePublicJwk](CachePublicJwk.md)

## Example

```typescript
import type { PrepareDirectoryVersionZipParameters } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "correlationId": null,
  "principal": null,
  "repositoryId": null,
  "directoryVersionId": null,
  "cachePublicKey": null,
} satisfies PrepareDirectoryVersionZipParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as PrepareDirectoryVersionZipParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


