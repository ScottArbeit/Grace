
# UploadSessionOperationEvent

Event cases whose payload is only an operation id.

## Properties

Name | Type
------------ | -------------
`eventCase` | string
`operationId` | string

## Example

```typescript
import type { UploadSessionOperationEvent } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "eventCase": null,
  "operationId": null,
} satisfies UploadSessionOperationEvent

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as UploadSessionOperationEvent
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


