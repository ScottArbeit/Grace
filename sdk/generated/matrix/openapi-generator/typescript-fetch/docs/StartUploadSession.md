
# StartUploadSession

Event payload recorded when a manifest upload session starts.

## Properties

Name | Type
------------ | -------------
`uploadSessionId` | string
`ownerId` | string
`organizationId` | string
`repositoryId` | string
`authorizedScope` | string
`fileContentHash` | string
`expectedSize` | number
`chunkingSuiteId` | string
`samplingPolicySnapshot` | string
`operationId` | string

## Example

```typescript
import type { StartUploadSession } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "uploadSessionId": null,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "organizationId": e35d64a9-b990-44f5-bf02-32ad7d15630c,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "authorizedScope": null,
  "fileContentHash": null,
  "expectedSize": null,
  "chunkingSuiteId": rabin-v1,
  "samplingPolicySnapshot": null,
  "operationId": null,
} satisfies StartUploadSession

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as StartUploadSession
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


