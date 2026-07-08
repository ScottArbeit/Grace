
# MaterializationTargetSelector

Public selector that the server resolves to one immutable root DirectoryVersionId.

## Properties

Name | Type
------------ | -------------
`_class` | string
`selectorKind` | [MaterializationTargetSelectorKind](MaterializationTargetSelectorKind.md)
`directoryVersionId` | string
`referenceId` | string
`branchName` | string

## Example

```typescript
import type { MaterializationTargetSelector } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": MaterializationTargetSelector,
  "selectorKind": null,
  "directoryVersionId": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "referenceId": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "branchName": MyBranch,
} satisfies MaterializationTargetSelector

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as MaterializationTargetSelector
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


