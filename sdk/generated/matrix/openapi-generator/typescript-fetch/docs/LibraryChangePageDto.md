
# LibraryChangePageDto


## Properties

Name | Type
------------ | -------------
`outcome` | [LibraryOutcomeKind](LibraryOutcomeKind.md)
`cursorEpoch` | string
`changes` | [Array&lt;LibraryChangeDto&gt;](LibraryChangeDto.md)
`lastCursor` | string
`hasMore` | boolean
`nextPageToken` | string
`rebaseline` | [LibraryRebaselineDto](LibraryRebaselineDto.md)

## Example

```typescript
import type { LibraryChangePageDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "outcome": null,
  "cursorEpoch": null,
  "changes": null,
  "lastCursor": null,
  "hasMore": null,
  "nextPageToken": null,
  "rebaseline": null,
} satisfies LibraryChangePageDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as LibraryChangePageDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


