
# EventMetadata


## Properties

Name | Type
------------ | -------------
`timestamp` | Date
`correlationId` | string
`principal` | string
`clientType` | string
`properties` | { [key: string]: string; }

## Example

```typescript
import type { EventMetadata } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "timestamp": null,
  "correlationId": null,
  "principal": null,
  "clientType": null,
  "properties": null,
} satisfies EventMetadata

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as EventMetadata
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


