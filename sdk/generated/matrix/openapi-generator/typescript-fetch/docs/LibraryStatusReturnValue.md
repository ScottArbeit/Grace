
# LibraryStatusReturnValue


## Properties

Name | Type
------------ | -------------
`eventTime` | Date
`correlationId` | string
`properties` | { [key: string]: string; }
`returnValue` | [LibraryRepositoryStatusDto](LibraryRepositoryStatusDto.md)

## Example

```typescript
import type { LibraryStatusReturnValue } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "eventTime": null,
  "correlationId": cli-20260604T181500Z-0001,
  "properties": null,
  "returnValue": null,
} satisfies LibraryStatusReturnValue

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryStatusReturnValue
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


