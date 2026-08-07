
# ReferenceReplayEventApiDto

One eligible Reference event paired with its opaque durable branch-event cursor.

## Properties

Name | Type
------------ | -------------
`eventCursor` | string
`reference` | [CurrentBranchReferenceNotification](CurrentBranchReferenceNotification.md)

## Example

```typescript
import type { ReferenceReplayEventApiDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "eventCursor": null,
  "reference": null,
} satisfies ReferenceReplayEventApiDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ReferenceReplayEventApiDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


