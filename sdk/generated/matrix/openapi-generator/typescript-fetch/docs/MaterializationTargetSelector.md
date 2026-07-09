
# MaterializationTargetSelector

Public selector that the server resolves to one immutable root DirectoryVersionId. ReferenceType selectors provide ReferenceType plus exactly one of BranchId or BranchName.

## Properties

Name | Type
------------ | -------------
`_class` | string
`selectorKind` | [MaterializationTargetSelectorKind](MaterializationTargetSelectorKind.md)
`directoryVersionId` | string
`referenceId` | string
`branchId` | string
`branchName` | string
`referenceType` | [ReferenceType](ReferenceType.md)

## Example

```typescript
import type { MaterializationTargetSelector } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": MaterializationTargetSelector,
  "selectorKind": null,
  "directoryVersionId": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "referenceId": c8f9bac8-d489-46c7-917f-b36b7d9efa9a,
  "branchId": de7bf47d-23ae-4599-af68-68a317ea390d,
  "branchName": MyBranch,
  "referenceType": null,
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


