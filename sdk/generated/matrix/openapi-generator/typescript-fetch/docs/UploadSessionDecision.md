
# UploadSessionDecision

UploadSession command decision returned by manifest upload-session endpoints.

## Properties

Name | Type
------------ | -------------
`session` | [UploadSessionDto](UploadSessionDto.md)
`operationId` | string
`events` | [Array&lt;UploadSessionEvent&gt;](UploadSessionEvent.md)
`wasIdempotentReplay` | boolean
`message` | string

## Example

```typescript
import type { UploadSessionDecision } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "session": null,
  "operationId": null,
  "events": null,
  "wasIdempotentReplay": null,
  "message": null,
} satisfies UploadSessionDecision

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as UploadSessionDecision
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


