
# BranchCommandReturnValue

Grace response envelope returned by branch command endpoints.

## Properties

Name | Type
------------ | -------------
`returnValue` | string
`eventTime` | Date
`correlationId` | string
`properties` | { [key: string]: string; }

## Example

```typescript
import type { BranchCommandReturnValue } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "returnValue": Branch command succeeded.,
  "eventTime": null,
  "correlationId": cli-20260604T181500Z-0001,
  "properties": null,
} satisfies BranchCommandReturnValue

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as BranchCommandReturnValue
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


