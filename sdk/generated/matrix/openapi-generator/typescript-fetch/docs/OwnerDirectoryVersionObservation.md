
# OwnerDirectoryVersionObservation

Retained completed DirectoryVersion metadata declarations. Zero is a completed reading, not missing data. This is not complete storage, elapsed usage or a charge.

## Properties

Name | Type
------------ | -------------
`observationId` | string
`scope` | [OwnerDirectoryVersionObservationScope](OwnerDirectoryVersionObservationScope.md)
`declaredLogicalBytes` | string
`distinctContentCount` | string
`enumerationStartedAt` | string
`enumerationFinishedAt` | string

## Example

```typescript
import type { OwnerDirectoryVersionObservation } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "observationId": null,
  "scope": null,
  "declaredLogicalBytes": 9007199254740993,
  "distinctContentCount": 0,
  "enumerationStartedAt": 2026-09-07T01:02:03.123456789Z,
  "enumerationFinishedAt": 2026-09-07T01:02:04.987654321Z,
} satisfies OwnerDirectoryVersionObservation

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as OwnerDirectoryVersionObservation
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


