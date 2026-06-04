
# ContentBlockDiscoveryPolicy

Bounded, non-authoritative discovery policy returned by /storage/discoverContentBlocks.

## Properties

Name | Type
------------ | -------------
`maxKeyChunkAddresses` | number
`maxCandidateWindowsPerKeyChunk` | number
`maxWindowChunks` | number
`maxResponseProtectedChunks` | number
`responseTtlSeconds` | number
`minimumAcceptedReuseRunLength` | number
`positiveCandidatesEnabled` | boolean
`emptyResponseMeansAbsent` | boolean
`isAuthoritative` | boolean

## Example

```typescript
import type { ContentBlockDiscoveryPolicy } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "maxKeyChunkAddresses": 256,
  "maxCandidateWindowsPerKeyChunk": 4,
  "maxWindowChunks": 256,
  "maxResponseProtectedChunks": 16384,
  "responseTtlSeconds": 300,
  "minimumAcceptedReuseRunLength": 8,
  "positiveCandidatesEnabled": null,
  "emptyResponseMeansAbsent": false,
  "isAuthoritative": false,
} satisfies ContentBlockDiscoveryPolicy

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ContentBlockDiscoveryPolicy
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


