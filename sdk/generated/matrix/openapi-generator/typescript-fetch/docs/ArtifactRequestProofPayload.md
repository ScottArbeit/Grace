
# ArtifactRequestProofPayload

Canonical statement binding one short-lived holder proof to an exact artifact request.

## Properties

Name | Type
------------ | -------------
`_class` | string
`grantDigest` | string
`httpMethod` | string
`normalizedRoute` | string
`artifactIdentity` | string
`issuedAt` | Date
`expiresAt` | Date

## Example

```typescript
import type { ArtifactRequestProofPayload } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "_class": ArtifactRequestProofPayload,
  "grantDigest": null,
  "httpMethod": GET,
  "normalizedRoute": /cache/artifacts/GraceZipFiles/root.zip,
  "artifactIdentity": null,
  "issuedAt": null,
  "expiresAt": null,
} satisfies ArtifactRequestProofPayload

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as ArtifactRequestProofPayload
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


