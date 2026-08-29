
# LibraryRebaselineDto


## Properties

Name | Type
------------ | -------------
`reason` | string
`currentEpoch` | string
`serviceFloorCursor` | string
`recommendedBootstrap` | boolean

## Example

```typescript
import type { LibraryRebaselineDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "reason": null,
  "currentEpoch": null,
  "serviceFloorCursor": null,
  "recommendedBootstrap": null,
} satisfies LibraryRebaselineDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryRebaselineDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


