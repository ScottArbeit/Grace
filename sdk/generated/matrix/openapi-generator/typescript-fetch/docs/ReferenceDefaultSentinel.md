
# ReferenceDefaultSentinel

Canonical ReferenceDto.Default value used only when a type-specific latest Reference does not yet exist.

## Properties

Name | Type
------------ | -------------
`_class` | string
`referenceId` | string
`ownerId` | string
`organizationId` | string
`repositoryId` | string
`branchId` | string
`directoryId` | string
`sha256Hash` | string
`blake3Hash` | string
`referenceType` | string
`referenceText` | string
`links` | Array&lt;string&gt;
`createdBy` | string
`createdAt` | string
`updatedAt` | string
`deletedAt` | string
`deleteReason` | string

## Example

```typescript
import type { ReferenceDefaultSentinel } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "referenceId": null,
  "ownerId": null,
  "organizationId": null,
  "repositoryId": null,
  "branchId": null,
  "directoryId": null,
  "sha256Hash": null,
  "blake3Hash": null,
  "referenceType": null,
  "referenceText": null,
  "links": null,
  "createdBy": null,
  "createdAt": null,
  "updatedAt": null,
  "deletedAt": null,
  "deleteReason": null,
} satisfies ReferenceDefaultSentinel

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ReferenceDefaultSentinel
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


