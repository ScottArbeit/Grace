
# UploadSessionEvent

Event emitted by the upload-session actor, paired with event metadata.

## Properties

Name | Type
------------ | -------------
`event` | [UploadSessionEventType](UploadSessionEventType.md)
`metadata` | [EventMetadata](EventMetadata.md)

## Example

```typescript
import type { UploadSessionEvent } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "event": null,
  "metadata": null,
} satisfies UploadSessionEvent

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as UploadSessionEvent
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


