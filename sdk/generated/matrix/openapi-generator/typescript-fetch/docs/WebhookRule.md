
# WebhookRule


## Properties

Name | Type
------------ | -------------
`_class` | string
`webhookRuleId` | string
`name` | string
`eventName` | string
`eventVersion` | number
`scope` | [WebhookScope](WebhookScope.md)
`url` | [ScopedOutboundUrl](ScopedOutboundUrl.md)
`signingSecretVersion` | string
`retryPolicy` | [WebhookRetryPolicy](WebhookRetryPolicy.md)
`status` | [WebhookRuleStatus](WebhookRuleStatus.md)
`createdBy` | string
`createdAt` | Date
`updatedAt` | Date

## Example

```typescript
import type { WebhookRule } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "webhookRuleId": null,
  "name": null,
  "eventName": null,
  "eventVersion": null,
  "scope": null,
  "url": null,
  "signingSecretVersion": null,
  "retryPolicy": null,
  "status": null,
  "createdBy": null,
  "createdAt": null,
  "updatedAt": null,
} satisfies WebhookRule

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as WebhookRule
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


