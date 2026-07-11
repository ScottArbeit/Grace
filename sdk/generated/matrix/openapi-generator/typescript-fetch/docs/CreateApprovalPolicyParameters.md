
# CreateApprovalPolicyParameters


## Properties

Name | Type
------------ | -------------
`correlationId` | string
`principal` | string
`properties` | { [key: string]: string; }
`ownerId` | string
`ownerName` | string
`organizationId` | string
`organizationName` | string
`repositoryId` | string
`repositoryName` | string
`targetBranchId` | string
`approvalPolicyId` | string
`name` | string
`subject` | string
`requiredResponder` | string
`notificationUrl` | string
`notificationUrlSafety` | [OutboundUrlSafety](OutboundUrlSafety.md)
`acknowledgeUnsafeLocalDevelopment` | boolean
`timeoutSeconds` | number
`onTimeout` | [ApprovalTimeoutAction](ApprovalTimeoutAction.md)

## Example

```typescript
import type { CreateApprovalPolicyParameters } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "correlationId": null,
  "principal": null,
  "properties": null,
  "ownerId": null,
  "ownerName": null,
  "organizationId": null,
  "organizationName": null,
  "repositoryId": null,
  "repositoryName": null,
  "targetBranchId": null,
  "approvalPolicyId": null,
  "name": null,
  "subject": promotion-set.apply,
  "requiredResponder": null,
  "notificationUrl": null,
  "notificationUrlSafety": null,
  "acknowledgeUnsafeLocalDevelopment": null,
  "timeoutSeconds": null,
  "onTimeout": null,
} satisfies CreateApprovalPolicyParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as CreateApprovalPolicyParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


