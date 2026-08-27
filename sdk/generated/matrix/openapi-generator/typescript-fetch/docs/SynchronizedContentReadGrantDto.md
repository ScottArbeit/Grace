
# SynchronizedContentReadGrantDto


## Properties

Name | Type
------------ | -------------
`grantId` | string
`downloadPath` | string
`content` | [SynchronizedContentVersionDto](SynchronizedContentVersionDto.md)
`expiresAt` | Date

## Example

```typescript
import type { SynchronizedContentReadGrantDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "grantId": null,
  "downloadPath": null,
  "content": null,
  "expiresAt": null,
} satisfies SynchronizedContentReadGrantDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedContentReadGrantDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


