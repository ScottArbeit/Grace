
# PromotionSetApprovalSummary


## Properties

Name | Type
------------ | -------------
`_class` | string
`promotionSetId` | string
`targetBranchId` | string
`stepsComputationAttempt` | number
`state` | [PromotionSetApprovalState](PromotionSetApprovalState.md)
`approvalRequestId` | string
`approvalPolicyId` | string
`requiredResponder` | string
`lastDecisionAt` | Date
`expiresAt` | Date
`reason` | string

## Example

```typescript
import type { PromotionSetApprovalSummary } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "promotionSetId": null,
  "targetBranchId": null,
  "stepsComputationAttempt": null,
  "state": null,
  "approvalRequestId": null,
  "approvalPolicyId": null,
  "requiredResponder": null,
  "lastDecisionAt": null,
  "expiresAt": null,
  "reason": null,
} satisfies PromotionSetApprovalSummary

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as PromotionSetApprovalSummary
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


