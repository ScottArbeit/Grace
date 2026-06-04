
# ApprovalPolicy


## Properties

Name | Type
------------ | -------------
`_class` | string
`approvalPolicyId` | string
`version` | number
`name` | string
`subject` | string
`scope` | [ApprovalScope](ApprovalScope.md)
`requiredResponder` | string
`notificationUrl` | [ScopedOutboundUrl](ScopedOutboundUrl.md)
`timeoutSeconds` | number
`onTimeout` | [ApprovalTimeoutAction](ApprovalTimeoutAction.md)
`status` | [ApprovalPolicyStatus](ApprovalPolicyStatus.md)
`createdBy` | string
`createdAt` | Date
`updatedAt` | Date

## Example

```typescript
import type { ApprovalPolicy } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "approvalPolicyId": null,
  "version": null,
  "name": null,
  "subject": null,
  "scope": null,
  "requiredResponder": null,
  "notificationUrl": null,
  "timeoutSeconds": null,
  "onTimeout": null,
  "status": null,
  "createdBy": null,
  "createdAt": null,
  "updatedAt": null,
} satisfies ApprovalPolicy

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ApprovalPolicy
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


