
# ApprovalScope


## Properties

Name | Type
------------ | -------------
`ownerId` | string
`organizationId` | string
`repositoryId` | string
`targetBranchId` | string
`promotionSetId` | string
`stepsComputationAttempt` | number
`approvalPolicyId` | string
`approvalPolicyVersion` | number

## Example

```typescript
import type { ApprovalScope } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "ownerId": null,
  "organizationId": null,
  "repositoryId": null,
  "targetBranchId": null,
  "promotionSetId": null,
  "stepsComputationAttempt": null,
  "approvalPolicyId": null,
  "approvalPolicyVersion": null,
} satisfies ApprovalScope

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ApprovalScope
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


