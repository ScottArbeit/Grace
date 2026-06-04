
# OrganizationReturnValue

Grace response envelope whose ReturnValue contains an organization DTO.

## Properties

Name | Type
------------ | -------------
`returnValue` | [OrganizationDto](OrganizationDto.md)
`eventTime` | Date
`correlationId` | string
`properties` | { [key: string]: string; }

## Example

```typescript
import type { OrganizationReturnValue } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "returnValue": null,
  "eventTime": null,
  "correlationId": cli-20260604T181500Z-0001,
  "properties": null,
} satisfies OrganizationReturnValue

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as OrganizationReturnValue
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


