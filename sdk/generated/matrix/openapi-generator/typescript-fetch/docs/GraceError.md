
# GraceError

Grace domain error envelope returned by command handlers. Authentication challenges and framework authorization failures use their HTTP-specific response shapes instead of this domain error envelope.

## Properties

Name | Type
------------ | -------------
`error` | string
`eventTime` | Date
`correlationId` | string
`properties` | { [key: string]: string; }

## Example

```typescript
import type { GraceError } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "error": OwnerId is required.,
  "eventTime": 2026-06-04T18:15:00Z,
  "correlationId": cli-20260604T181500Z-0001,
  "properties": null,
} satisfies GraceError

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as GraceError
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


