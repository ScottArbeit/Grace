
# WebhookDelivery


## Properties

Name | Type
------------ | -------------
`_class` | string
`webhookDeliveryId` | string
`webhookRuleId` | string
`eventName` | string
`eventVersion` | number
`dedupeKey` | string
`status` | [WebhookDeliveryStatus](WebhookDeliveryStatus.md)
`attemptCount` | number
`lastAttemptAt` | Date
`nextAttemptAt` | Date
`lastStatusCode` | number
`lastError` | string
`createdAt` | Date

## Example

```typescript
import type { WebhookDelivery } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "webhookDeliveryId": null,
  "webhookRuleId": null,
  "eventName": null,
  "eventVersion": null,
  "dedupeKey": null,
  "status": null,
  "attemptCount": null,
  "lastAttemptAt": null,
  "nextAttemptAt": null,
  "lastStatusCode": null,
  "lastError": null,
  "createdAt": null,
} satisfies WebhookDelivery

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as WebhookDelivery
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


