
# BranchAnnotationApiDto

Branch annotation result for an existing server-known reference.

## Properties

Name | Type
------------ | -------------
`_class` | string
`requestedLineRange` | [AnnotationLineRange](AnnotationLineRange.md)
`targetReferenceId` | string
`path` | string
`referenceTypeFilter` | [Array&lt;ReferenceType&gt;](ReferenceType.md)
`maxReferences` | number
`includeLineText` | boolean
`lines` | [Array&lt;AnnotationLine&gt;](AnnotationLine.md)
`boundaries` | [Array&lt;AnnotationBoundary&gt;](AnnotationBoundary.md)
`spans` | [Array&lt;AnnotationSpan&gt;](AnnotationSpan.md)
`sourceRows` | [Array&lt;AnnotationSourceRow&gt;](AnnotationSourceRow.md)
`sourceReferences` | [Array&lt;AnnotationSourceReference&gt;](AnnotationSourceReference.md)

## Example

```typescript
import type { BranchAnnotationApiDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": BranchAnnotationDto,
  "requestedLineRange": null,
  "targetReferenceId": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "path": null,
  "referenceTypeFilter": null,
  "maxReferences": null,
  "includeLineText": null,
  "lines": null,
  "boundaries": null,
  "spans": null,
  "sourceRows": null,
  "sourceReferences": null,
} satisfies BranchAnnotationApiDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as BranchAnnotationApiDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


