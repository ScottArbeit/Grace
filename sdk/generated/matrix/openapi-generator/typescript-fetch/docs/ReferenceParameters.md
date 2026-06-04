
# ReferenceParameters


## Properties

Name | Type
------------ | -------------
`correlationId` | string
`principal` | string
`referenceId` | string
`referenceType` | [ReferenceType](ReferenceType.md)
`referenceText` | string
`repositoryId` | string
`repositoryName` | string

## Example

```typescript
import type { ReferenceParameters } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "correlationId": null,
  "principal": null,
  "referenceId": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "referenceType": null,
  "referenceText": null,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "repositoryName": null,
} satisfies ReferenceParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ReferenceParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


