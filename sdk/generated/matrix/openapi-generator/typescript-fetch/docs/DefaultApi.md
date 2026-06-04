# DefaultApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**claimReuseRanges**](DefaultApi.md#claimreuseranges) | **POST** /storage/claimReuseRanges | Claims reusable ContentBlock ranges. |
| [**confirmContentBlockUpload**](DefaultApi.md#confirmcontentblockupload) | **POST** /storage/confirmContentBlockUpload | Confirms a ContentBlock upload. |
| [**discoverContentBlocks**](DefaultApi.md#discovercontentblocks) | **POST** /storage/discoverContentBlocks | Discovers reusable ContentBlock candidates. |
| [**finalizeManifestUpload**](DefaultApi.md#finalizemanifestupload) | **POST** /storage/finalizeManifestUpload | Finalizes a manifest upload. |
| [**getContentBlockDownloadUri**](DefaultApi.md#getcontentblockdownloaduri) | **POST** /storage/getContentBlockDownloadUri | Gets a download URI for a ContentBlock payload. |
| [**getContentBlockUploadUri**](DefaultApi.md#getcontentblockuploaduri) | **POST** /storage/getContentBlockUploadUri | Gets an upload URI for a ContentBlock payload. |
| [**getDownloadUri**](DefaultApi.md#getdownloaduri) | **POST** /storage/getDownloadUri | Gets a download URI for whole-file content. |
| [**getOpenApi**](DefaultApi.md#getopenapi) | **GET** /openApi | Get the OpenAPI specification for the Grace Server API |
| [**getUploadMetadataForFiles**](DefaultApi.md#getuploadmetadataforfiles) | **POST** /storage/getUploadMetadataForFiles | Gets upload metadata for files. |
| [**getUploadUri**](DefaultApi.md#getuploaduri) | **POST** /storage/getUploadUri | Gets upload URIs for whole-file content. |
| [**issueDedupeDiscovery**](DefaultApi.md#issuededupediscovery) | **POST** /storage/issueDedupeDiscovery | Issues upload-session dedupe discovery hints. |
| [**registerContentBlockUpload**](DefaultApi.md#registercontentblockupload) | **POST** /storage/registerContentBlockUpload | Registers a ContentBlock upload intent. |
| [**startManifestUploadSession**](DefaultApi.md#startmanifestuploadsession) | **POST** /storage/startManifestUploadSession | Starts a manifest upload session. |



## claimReuseRanges

> UploadSessionDecisionReturnValue claimReuseRanges(claimReuseRangesParameters)

Claims reusable ContentBlock ranges.

Claims server-issued candidate ranges after authoritative ContentBlockMetadata validation.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ClaimReuseRangesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // ClaimReuseRangesParameters
    claimReuseRangesParameters: ...,
  } satisfies ClaimReuseRangesRequest;

  try {
    const data = await api.claimReuseRanges(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **claimReuseRangesParameters** | [ClaimReuseRangesParameters](ClaimReuseRangesParameters.md) |  | |

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## confirmContentBlockUpload

> UploadSessionDecisionReturnValue confirmContentBlockUpload(confirmContentBlockUploadParameters)

Confirms a ContentBlock upload.

Confirms object-storage placement for a ContentBlock that was uploaded for this manifest session.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ConfirmContentBlockUploadRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // ConfirmContentBlockUploadParameters
    confirmContentBlockUploadParameters: ...,
  } satisfies ConfirmContentBlockUploadRequest;

  try {
    const data = await api.confirmContentBlockUpload(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **confirmContentBlockUploadParameters** | [ConfirmContentBlockUploadParameters](ConfirmContentBlockUploadParameters.md) |  | |

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## discoverContentBlocks

> DiscoverContentBlocksReturnValue discoverContentBlocks(discoverContentBlocksParameters)

Discovers reusable ContentBlock candidates.

Returns bounded, non-authoritative candidate windows for key chunks; empty results do not prove absence.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DiscoverContentBlocksRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // DiscoverContentBlocksParameters
    discoverContentBlocksParameters: ...,
  } satisfies DiscoverContentBlocksRequest;

  try {
    const data = await api.discoverContentBlocks(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **discoverContentBlocksParameters** | [DiscoverContentBlocksParameters](DiscoverContentBlocksParameters.md) |  | |

### Return type

[**DiscoverContentBlocksReturnValue**](DiscoverContentBlocksReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## finalizeManifestUpload

> UploadSessionDecisionReturnValue finalizeManifestUpload(finalizeManifestUploadParameters)

Finalizes a manifest upload.

Finalizes the manifest after validating reconstruction, range claims, block presence, file hash, and size.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { FinalizeManifestUploadRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // FinalizeManifestUploadParameters
    finalizeManifestUploadParameters: ...,
  } satisfies FinalizeManifestUploadRequest;

  try {
    const data = await api.finalizeManifestUpload(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **finalizeManifestUploadParameters** | [FinalizeManifestUploadParameters](FinalizeManifestUploadParameters.md) |  | |

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getContentBlockDownloadUri

> string getContentBlockDownloadUri(getContentBlockDownloadUriParameters)

Gets a download URI for a ContentBlock payload.

Gets an object-storage download URI for an authorized manifest-backed reconstruction path.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetContentBlockDownloadUriRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // GetContentBlockDownloadUriParameters
    getContentBlockDownloadUriParameters: ...,
  } satisfies GetContentBlockDownloadUriRequest;

  try {
    const data = await api.getContentBlockDownloadUri(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getContentBlockDownloadUriParameters** | [GetContentBlockDownloadUriParameters](GetContentBlockDownloadUriParameters.md) |  | |

### Return type

**string**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `text/plain`, `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getContentBlockUploadUri

> string getContentBlockUploadUri(getContentBlockUploadUriParameters)

Gets an upload URI for a ContentBlock payload.

Gets an object-storage upload URI without probing whether the ContentBlock already exists.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetContentBlockUploadUriRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // GetContentBlockUploadUriParameters
    getContentBlockUploadUriParameters: ...,
  } satisfies GetContentBlockUploadUriRequest;

  try {
    const data = await api.getContentBlockUploadUri(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getContentBlockUploadUriParameters** | [GetContentBlockUploadUriParameters](GetContentBlockUploadUriParameters.md) |  | |

### Return type

**string**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `text/plain`, `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getDownloadUri

> string getDownloadUri(getDownloadUriParameters)

Gets a download URI for whole-file content.

Compatibility endpoint for downloading repository-scoped WholeFileContent.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetDownloadUriRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // GetDownloadUriParameters
    getDownloadUriParameters: ...,
  } satisfies GetDownloadUriRequest;

  try {
    const data = await api.getDownloadUri(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getDownloadUriParameters** | [GetDownloadUriParameters](GetDownloadUriParameters.md) |  | |

### Return type

**string**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `text/plain`, `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getOpenApi

> getOpenApi()

Get the OpenAPI specification for the Grace Server API

This endpoint returns the OpenAPI specification for the Grace Server API. The specification is generated from the OpenAPI specification files in the src/OpenAPI folder.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetOpenApiRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  try {
    const data = await api.getOpenApi();
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters

This endpoint does not need any parameter.

### Return type

`void` (Empty response body)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: Not defined
- **Accept**: Not defined


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getUploadMetadataForFiles

> UploadMetadataArrayReturnValue getUploadMetadataForFiles(getUploadMetadataForFilesParameters)

Gets upload metadata for files.

Gets upload URLs and content-reference metadata for whole-file compatibility uploads.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetUploadMetadataForFilesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // GetUploadMetadataForFilesParameters
    getUploadMetadataForFilesParameters: ...,
  } satisfies GetUploadMetadataForFilesRequest;

  try {
    const data = await api.getUploadMetadataForFiles(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getUploadMetadataForFilesParameters** | [GetUploadMetadataForFilesParameters](GetUploadMetadataForFilesParameters.md) |  | |

### Return type

[**UploadMetadataArrayReturnValue**](UploadMetadataArrayReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getUploadUri

> { [key: string]: string; } getUploadUri(getUploadUriParameters)

Gets upload URIs for whole-file content.

Compatibility endpoint for uploading repository-scoped WholeFileContent.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetUploadUriRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // GetUploadUriParameters
    getUploadUriParameters: ...,
  } satisfies GetUploadUriRequest;

  try {
    const data = await api.getUploadUri(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getUploadUriParameters** | [GetUploadUriParameters](GetUploadUriParameters.md) |  | |

### Return type

**{ [key: string]: string; }**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## issueDedupeDiscovery

> UploadSessionDecisionReturnValue issueDedupeDiscovery(issueDedupeDiscoveryParameters)

Issues upload-session dedupe discovery hints.

Records server-issued discovery hints for a started upload session before reuse claims.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { IssueDedupeDiscoveryRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // IssueDedupeDiscoveryParameters
    issueDedupeDiscoveryParameters: ...,
  } satisfies IssueDedupeDiscoveryRequest;

  try {
    const data = await api.issueDedupeDiscovery(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **issueDedupeDiscoveryParameters** | [IssueDedupeDiscoveryParameters](IssueDedupeDiscoveryParameters.md) |  | |

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## registerContentBlockUpload

> UploadSessionDecisionReturnValue registerContentBlockUpload(registerContentBlockUploadParameters)

Registers a ContentBlock upload intent.

Records the expected logical range and payload size before uploading a new ContentBlock.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RegisterContentBlockUploadRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // RegisterContentBlockUploadParameters
    registerContentBlockUploadParameters: ...,
  } satisfies RegisterContentBlockUploadRequest;

  try {
    const data = await api.registerContentBlockUpload(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **registerContentBlockUploadParameters** | [RegisterContentBlockUploadParameters](RegisterContentBlockUploadParameters.md) |  | |

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## startManifestUploadSession

> UploadSessionDecisionReturnValue startManifestUploadSession(startManifestUploadSessionParameters)

Starts a manifest upload session.

Starts the server-authoritative workflow for one manifest-backed file.

### Example

```ts
import {
  Configuration,
  DefaultApi,
} from '@grace-vcs/generated-openapi-probe';
import type { StartManifestUploadSessionRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DefaultApi(config);

  const body = {
    // StartManifestUploadSessionParameters
    startManifestUploadSessionParameters: ...,
  } satisfies StartManifestUploadSessionRequest;

  try {
    const data = await api.startManifestUploadSession(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **startManifestUploadSessionParameters** | [StartManifestUploadSessionParameters](StartManifestUploadSessionParameters.md) |  | |

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)

