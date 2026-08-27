
# CacheArtifactGrantValidationKeyReturnValue

Grace response envelope containing the current Server process validation key.

## Properties

Name | Type
------------ | -------------
`returnValue` | [CacheArtifactGrantValidationKey](CacheArtifactGrantValidationKey.md)
`eventTime` | Date
`correlationId` | string
`properties` | { [key: string]: string; }

## Example

```typescript
import type { CacheArtifactGrantValidationKeyReturnValue } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "returnValue": null,
  "eventTime": null,
  "correlationId": null,
  "properties": null,
} satisfies CacheArtifactGrantValidationKeyReturnValue

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CacheArtifactGrantValidationKeyReturnValue
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


