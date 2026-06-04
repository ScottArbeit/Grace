
# FileDiff


## Properties

Name | Type
------------ | -------------
`relativePath` | any
`fileSha1` | any
`createdAt1` | any
`fileSha2` | any
`createdAt2` | any
`isBinary` | boolean
`inlineDiff` | Array&lt;Array&lt;any&gt;&gt;
`sideBySideOld` | Array&lt;Array&lt;any&gt;&gt;
`sideBySideNew` | Array&lt;Array&lt;any&gt;&gt;

## Example

```typescript
import type { FileDiff } from '@grace-vcs/generated-openapi-probe'

// TODO: Update the object below with actual values
const example = {
  "relativePath": null,
  "fileSha1": null,
  "createdAt1": null,
  "fileSha2": null,
  "createdAt2": null,
  "isBinary": null,
  "inlineDiff": null,
  "sideBySideOld": null,
  "sideBySideNew": null,
} satisfies FileDiff

console.log(example)

// Convert the instance to a JSON string
const exampleJSON: string = JSON.stringify(example)
console.log(exampleJSON)

// Parse the JSON string back to an object
const exampleParsed = JSON.parse(exampleJSON) as FileDiff
console.log(exampleParsed)
```

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


