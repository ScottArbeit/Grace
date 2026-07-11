
# IssueDedupeDiscoveryParameters


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
`operationId` | string
`expiresAt` | Date
`minimumReuseRunLength` | number
`keyChunkAddresses` | Array&lt;string&gt;
`hints` | [Array&lt;ContentBlockReuseRangeHint&gt;](ContentBlockReuseRangeHint.md)

## Example

```typescript
import type { IssueDedupeDiscoveryParameters } from '@grace-vcs/generated-openapi-probe'

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
  "operationId": null,
  "expiresAt": null,
  "minimumReuseRunLength": null,
  "keyChunkAddresses": null,
  "hints": null,
} satisfies IssueDedupeDiscoveryParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as IssueDedupeDiscoveryParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


