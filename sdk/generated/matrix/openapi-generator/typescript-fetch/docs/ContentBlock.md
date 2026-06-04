
# ContentBlock

Logical byte range inside a manifest-backed file.

## Properties

Name | Type
------------ | -------------
`_class` | string
`address` | string
`offset` | number
`size` | number

## Example

```typescript
import type { ContentBlock } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "address": null,
  "offset": null,
  "size": null,
} satisfies ContentBlock

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ContentBlock
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


