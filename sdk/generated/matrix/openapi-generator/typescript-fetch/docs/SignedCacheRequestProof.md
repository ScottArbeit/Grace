
# SignedCacheRequestProof


## Properties

Name | Type
------------ | -------------
`_class` | string
`payload` | [CacheRequestProofPayload](CacheRequestProofPayload.md)
`signature` | string

## Example

```typescript
import type { SignedCacheRequestProof } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": SignedCacheRequestProof,
  "payload": null,
  "signature": null,
} satisfies SignedCacheRequestProof

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SignedCacheRequestProof
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


