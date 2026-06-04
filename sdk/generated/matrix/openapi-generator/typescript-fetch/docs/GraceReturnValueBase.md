
# GraceReturnValueBase

Common metadata returned with successful Grace responses.

## Properties

Name | Type
------------ | -------------
`eventTime` | Date
`correlationId` | string
`properties` | { [key: string]: any; }

## Example

```typescript
import type { GraceReturnValueBase } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "eventTime": null,
  "correlationId": null,
  "properties": null,
} satisfies GraceReturnValueBase

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as GraceReturnValueBase
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


