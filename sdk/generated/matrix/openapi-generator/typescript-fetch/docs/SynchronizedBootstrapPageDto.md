
# SynchronizedBootstrapPageDto


## Properties

Name | Type
------------ | -------------
`bootstrapId` | string
`boundaryCursor` | string
`cursorEpoch` | string
`rootConfiguration` | [SynchronizedRootConfigurationDto](SynchronizedRootConfigurationDto.md)
`items` | [Array&lt;SynchronizedItemDto&gt;](SynchronizedItemDto.md)
`nextPageToken` | string

## Example

```typescript
import type { SynchronizedBootstrapPageDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "bootstrapId": null,
  "boundaryCursor": null,
  "cursorEpoch": null,
  "rootConfiguration": null,
  "items": null,
  "nextPageToken": null,
} satisfies SynchronizedBootstrapPageDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as SynchronizedBootstrapPageDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


