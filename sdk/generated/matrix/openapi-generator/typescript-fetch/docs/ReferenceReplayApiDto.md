
# ReferenceReplayApiDto

Eligible Reference events and the exact closure of one immutable branch-event snapshot.

## Properties

Name | Type
------------ | -------------
`repositoryId` | string
`branchId` | string
`events` | [Array&lt;ReferenceReplayEventApiDto&gt;](ReferenceReplayEventApiDto.md)
`scannedThroughCursor` | string

## Example

```typescript
import type { ReferenceReplayApiDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "branchId": de7bf47d-23ae-4599-af68-68a317ea390d,
  "events": null,
  "scannedThroughCursor": null,
} satisfies ReferenceReplayApiDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ReferenceReplayApiDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


