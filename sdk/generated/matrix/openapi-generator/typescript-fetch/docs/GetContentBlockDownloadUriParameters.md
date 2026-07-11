
# GetContentBlockDownloadUriParameters


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
`referenceId` | string
`authorizedScope` | string
`sha256Hash` | string
`blake3Hash` | string
`storagePoolId` | string
`contentBlockAddress` | string
`manifestAddress` | string

## Example

```typescript
import type { GetContentBlockDownloadUriParameters } from '@grace-vcs/generated-openapi-probe'

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
  "referenceId": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "authorizedScope": null,
  "sha256Hash": 805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243,
  "blake3Hash": 9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d,
  "storagePoolId": null,
  "contentBlockAddress": null,
  "manifestAddress": null,
} satisfies GetContentBlockDownloadUriParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as GetContentBlockDownloadUriParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


