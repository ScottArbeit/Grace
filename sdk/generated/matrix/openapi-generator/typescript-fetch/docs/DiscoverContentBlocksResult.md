
# DiscoverContentBlocksResult

Non-authoritative ContentBlock discovery result.

## Properties

Name | Type
------------ | -------------
`requestedKeyChunkCount` | number
`acceptedKeyChunkCount` | number
`policy` | [ContentBlockDiscoveryPolicy](ContentBlockDiscoveryPolicy.md)
`candidateContentBlocks` | [Array&lt;ContentBlockDiscoveryCandidate&gt;](ContentBlockDiscoveryCandidate.md)
`isPartial` | boolean
`message` | string

## Example

```typescript
import type { DiscoverContentBlocksResult } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "requestedKeyChunkCount": null,
  "acceptedKeyChunkCount": null,
  "policy": null,
  "candidateContentBlocks": null,
  "isPartial": null,
  "message": null,
} satisfies DiscoverContentBlocksResult

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as DiscoverContentBlocksResult
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


