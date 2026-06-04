
# FinalizeManifestBlockPayload

Optional inline payload used by manifest finalization compatibility paths.

## Properties

Name | Type
------------ | -------------
`address` | string
`payload` | string

## Example

```typescript
import type { FinalizeManifestBlockPayload } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "address": null,
  "payload": null,
} satisfies FinalizeManifestBlockPayload

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as FinalizeManifestBlockPayload
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


