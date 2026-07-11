
# WebhookRuleParameters


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
`webhookRuleId` | string

## Example

```typescript
import type { WebhookRuleParameters } from '@grace-vcs/generated-openapi-probe'

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
  "webhookRuleId": null,
} satisfies WebhookRuleParameters

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as WebhookRuleParameters
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


