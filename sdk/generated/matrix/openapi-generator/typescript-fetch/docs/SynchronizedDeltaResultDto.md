
# SynchronizedDeltaResultDto


## Properties

Name | Type
------------ | -------------
`outcome` | [SynchronizedOutcomeKind](SynchronizedOutcomeKind.md)
`cursorEpoch` | string
`mutations` | [Array&lt;SynchronizedMutationDto&gt;](SynchronizedMutationDto.md)
`lastCursor` | string
`hasMore` | boolean
`nextPageToken` | string
`rebaseline` | [SynchronizedRebaselineDto](SynchronizedRebaselineDto.md)

## Example

```typescript
import type { SynchronizedDeltaResultDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "outcome": null,
  "cursorEpoch": null,
  "mutations": null,
  "lastCursor": null,
  "hasMore": null,
  "nextPageToken": null,
  "rebaseline": null,
} satisfies SynchronizedDeltaResultDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedDeltaResultDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


