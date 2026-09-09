
# OwnerObservationReturnValue


## Properties

Name | Type
------------ | -------------
`returnValue` | [OwnerDirectoryVersionObservation](OwnerDirectoryVersionObservation.md)
`eventTime` | Date
`correlationId` | string
`properties` | { [key: string]: any; }

## Example

```typescript
import type { OwnerObservationReturnValue } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "returnValue": null,
  "eventTime": null,
  "correlationId": cli-20260604T181500Z-0001,
  "properties": null,
} satisfies OwnerObservationReturnValue

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as OwnerObservationReturnValue
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


