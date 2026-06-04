
# UploadSessionDto

Server state snapshot for a manifest upload session.

## Properties

Name | Type
------------ | -------------
`_class` | string
`uploadSessionId` | string
`ownerId` | string
`organizationId` | string
`repositoryId` | string
`authorizedScope` | string
`fileContentHash` | string
`expectedSize` | number
`chunkingSuiteId` | string
`samplingPolicySnapshot` | string
`lifecycleState` | [UploadSessionLifecycleState](UploadSessionLifecycleState.md)
`startedAt` | Date
`completedAt` | Date
`finalizedManifestAddress` | string
`blockUploadIntents` | [Array&lt;BlockUploadIntent&gt;](BlockUploadIntent.md)
`confirmedBlockUploads` | [Array&lt;ConfirmedBlockUpload&gt;](ConfirmedBlockUpload.md)
`dedupeDiscovery` | [DedupeDiscoverySnapshot](DedupeDiscoverySnapshot.md)
`claimedReuseRanges` | [Array&lt;ClaimedReuseRange&gt;](ClaimedReuseRange.md)
`cleanupReminderScheduledAt` | Date
`cleanupReminderOperationId` | string
`lastOperationId` | string

## Example

```typescript
import type { UploadSessionDto } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": null,
  "uploadSessionId": null,
  "ownerId": 9dd5f81f-dc43-4839-9173-85d09394f30f,
  "organizationId": e35d64a9-b990-44f5-bf02-32ad7d15630c,
  "repositoryId": ab6f35ef-6e01-440b-8f9b-c343a5272095,
  "authorizedScope": null,
  "fileContentHash": null,
  "expectedSize": null,
  "chunkingSuiteId": rabin-v1,
  "samplingPolicySnapshot": null,
  "lifecycleState": null,
  "startedAt": null,
  "completedAt": null,
  "finalizedManifestAddress": null,
  "blockUploadIntents": null,
  "confirmedBlockUploads": null,
  "dedupeDiscovery": null,
  "claimedReuseRanges": null,
  "cleanupReminderScheduledAt": null,
  "cleanupReminderOperationId": null,
  "lastOperationId": null,
} satisfies UploadSessionDto

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as UploadSessionDto
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


