
# ArtifactGrantPayload

Requester-, holder-, cache-, root-, mode-, and artifact-bound authorization payload.

## Properties

Name | Type
------------ | -------------
`_class` | string
`issuer` | string
`requesterPrincipalType` | [ArtifactGrantRequesterPrincipalType](ArtifactGrantRequesterPrincipalType.md)
`requesterPrincipalId` | string
`holderKeyThumbprint` | string
`cacheId` | string
`targetRootDirectoryVersionId` | string
`executionMode` | [MaterializationExecutionMode](MaterializationExecutionMode.md)
`artifactIdentities` | Array&lt;string&gt;
`issuedAt` | Date
`notBefore` | Date
`expiresAt` | Date

## Example

```typescript
import type { ArtifactGrantPayload } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": ArtifactGrantPayload,
  "issuer": Grace.Server.ArtifactGrant.v1,
  "requesterPrincipalType": null,
  "requesterPrincipalId": null,
  "holderKeyThumbprint": null,
  "cacheId": null,
  "targetRootDirectoryVersionId": 33a4e36b-828f-4fae-9343-50b6560dc842,
  "executionMode": null,
  "artifactIdentities": null,
  "issuedAt": null,
  "notBefore": null,
  "expiresAt": null,
} satisfies ArtifactGrantPayload

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ArtifactGrantPayload
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


