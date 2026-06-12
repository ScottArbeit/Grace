
# DirectoryVersionSha256HashLookupReturnValue

Grace response envelope whose ReturnValue contains a SHA-256 lookup result or no-match sentinel.

## Properties

Name | Type
------------ | -------------
`returnValue` | [DirectoryVersionHashLookupResult](DirectoryVersionHashLookupResult.md)
`eventTime` | Date
`correlationId` | string
`properties` | { [key: string]: string; }

## Example

```typescript
import type { DirectoryVersionSha256HashLookupReturnValue } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "returnValue": null,
  "eventTime": null,
  "correlationId": cli-20260604T181500Z-0001,
  "properties": null,
} satisfies DirectoryVersionSha256HashLookupReturnValue

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as DirectoryVersionSha256HashLookupReturnValue
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


