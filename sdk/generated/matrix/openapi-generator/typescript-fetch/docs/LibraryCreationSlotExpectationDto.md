
# LibraryCreationSlotExpectationDto


## Properties

Name | Type
------------ | -------------
`parent` | [LibraryParentDto](LibraryParentDto.md)
`name` | string
`expectedSlotVersion` | string
`expectedState` | string

## Example

```typescript
import type { LibraryCreationSlotExpectationDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "parent": null,
  "name": null,
  "expectedSlotVersion": null,
  "expectedState": null,
} satisfies LibraryCreationSlotExpectationDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryCreationSlotExpectationDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


