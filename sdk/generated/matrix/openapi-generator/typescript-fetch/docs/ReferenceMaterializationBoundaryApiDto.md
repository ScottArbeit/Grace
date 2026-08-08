
# ReferenceMaterializationBoundaryApiDto

Exact materialized root identity coupled to an opaque server-ordered branch event cursor.

## Properties

Name | Type
------------ | -------------
`repositoryId` | string
`branchId` | string
`directoryId` | string
`sha256Hash` | string
`blake3Hash` | string
`eventCursor` | string

## Example

```typescript
import type { ReferenceMaterializationBoundaryApiDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "branchId": de7bf47d-23ae-4599-af68-68a317ea390d,
  "directoryId": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "sha256Hash": 805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243,
  "blake3Hash": 9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d,
  "eventCursor": null,
} satisfies ReferenceMaterializationBoundaryApiDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ReferenceMaterializationBoundaryApiDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


