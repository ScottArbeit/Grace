
# RegisterContentBlockUploadParameters


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
`uploadSessionId` | string
`authorizedScope` | string
`operationId` | string
`contentBlockAddress` | string
`logicalOffset` | number
`logicalLength` | number
`expectedPayloadLength` | number

## Example

```typescript
import type { RegisterContentBlockUploadParameters } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "correlationId": null,
  "principal": null,
  "ownerId": null,
  "ownerName": null,
  "organizationId": null,
  "organizationName": null,
  "repositoryId": null,
  "repositoryName": null,
  "uploadSessionId": null,
  "authorizedScope": null,
  "operationId": null,
  "contentBlockAddress": null,
  "logicalOffset": null,
  "logicalLength": null,
  "expectedPayloadLength": null,
} satisfies RegisterContentBlockUploadParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as RegisterContentBlockUploadParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


