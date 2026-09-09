
# LibraryContentReadDto


## Properties

Name | Type
------------ | -------------
`downloadPath` | string
`content` | [LibraryContentVersionDto](LibraryContentVersionDto.md)
`expiresAt` | Date

## Example

```typescript
import type { LibraryContentReadDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "downloadPath": null,
  "content": null,
  "expiresAt": null,
} satisfies LibraryContentReadDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryContentReadDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


