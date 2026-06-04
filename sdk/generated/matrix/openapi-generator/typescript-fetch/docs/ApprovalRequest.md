
# ApprovalRequest


## Properties

Name | Type
------------ | -------------
`_class` | string
`approvalRequestId` | string
`approvalPolicyId` | string
`approvalPolicyVersion` | number
`subject` | string
`scope` | [ApprovalScope](ApprovalScope.md)
`requiredResponder` | string
`status` | [ApprovalRequestStatus](ApprovalRequestStatus.md)
`decision` | [ApprovalRequestDecision](ApprovalRequestDecision.md)
`createdBy` | string
`createdAt` | Date
`expiresAt` | Date
`updatedAt` | Date
`supersededByApprovalRequestId` | string

## Example

```typescript
import type { ApprovalRequest } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "approvalRequestId": null,
  "approvalPolicyId": null,
  "approvalPolicyVersion": null,
  "subject": null,
  "scope": null,
  "requiredResponder": null,
  "status": null,
  "decision": null,
  "createdBy": null,
  "createdAt": null,
  "expiresAt": null,
  "updatedAt": null,
  "supersededByApprovalRequestId": null,
} satisfies ApprovalRequest

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ApprovalRequest
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


