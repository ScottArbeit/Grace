
# AnnotationSourceRow

Source row range for an annotation span.

## Properties

Name | Type
------------ | -------------
`sourceRowId` | string
`sourceReferenceId` | string
`path` | string
`lineRange` | [AnnotationLineRange](AnnotationLineRange.md)

## Example

```typescript
import type { AnnotationSourceRow } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "sourceRowId": null,
  "sourceReferenceId": null,
  "path": null,
  "lineRange": null,
} satisfies AnnotationSourceRow

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as AnnotationSourceRow
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


