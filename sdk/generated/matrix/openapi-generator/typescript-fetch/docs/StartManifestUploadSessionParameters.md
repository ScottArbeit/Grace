
# StartManifestUploadSessionParameters


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
`uploadSessionId` | string
`authorizedScope` | string
`fileContentHash` | string
`expectedSize` | number
`chunkingSuiteId` | string
`samplingPolicySnapshot` | string
`operationId` | string

## Example

```typescript
import type { StartManifestUploadSessionParameters } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "correlationId": null,
  "principal": null,
  "properties": null,
  "ownerId": null,
  "ownerName": null,
  "organizationId": null,
  "organizationName": null,
  "repositoryId": null,
  "repositoryName": null,
  "uploadSessionId": null,
  "authorizedScope": null,
  "fileContentHash": null,
  "expectedSize": null,
  "chunkingSuiteId": rabin-v1,
  "samplingPolicySnapshot": null,
  "operationId": null,
} satisfies StartManifestUploadSessionParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as StartManifestUploadSessionParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


