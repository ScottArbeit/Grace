
# AnnotationSpan

Annotated target span and its source rows.

## Properties

Name | Type
------------ | -------------
`spanId` | string
`boundaryId` | string
`lineRange` | [AnnotationLineRange](AnnotationLineRange.md)
`sourceRowIds` | Array&lt;string&gt;

## Example

```typescript
import type { AnnotationSpan } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "spanId": null,
  "boundaryId": null,
  "lineRange": null,
  "sourceRowIds": null,
} satisfies AnnotationSpan

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as AnnotationSpan
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


