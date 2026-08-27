
# SynchronizedCreationSlotExpectationDto


## Properties

Name | Type
------------ | -------------
`parent` | [SynchronizedParentDto](SynchronizedParentDto.md)
`name` | string
`expectedSlotVersion` | string
`expectedState` | string

## Example

```typescript
import type { SynchronizedCreationSlotExpectationDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "parent": null,
  "name": null,
  "expectedSlotVersion": null,
  "expectedState": null,
} satisfies SynchronizedCreationSlotExpectationDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedCreationSlotExpectationDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


