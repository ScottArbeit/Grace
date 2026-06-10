
# AnnotationSourceReference

Reference that contributed annotation source rows.

## Properties

Name | Type
------------ | -------------
`sourceReferenceId` | string
`referenceId` | string
`referenceType` | [ReferenceType](ReferenceType.md)
`referenceText` | string
`directoryVersionId` | string
`createdAt` | Date
`createdBy` | string

## Example

```typescript
import type { AnnotationSourceReference } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "sourceReferenceId": null,
  "referenceId": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "referenceType": null,
  "referenceText": null,
  "directoryVersionId": null,
  "createdAt": null,
  "createdBy": null,
} satisfies AnnotationSourceReference

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as AnnotationSourceReference
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


