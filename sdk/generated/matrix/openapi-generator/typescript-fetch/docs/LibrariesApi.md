# LibrariesApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**addLibrary**](LibrariesApi.md#addlibrary) | **POST** /libraries/add | Add one empty normalized Library under an exact configuration version. |
| [**continueLibraryBootstrap**](LibrariesApi.md#continuelibrarybootstrap) | **POST** /libraries/bootstrap/continue | Continue one immutable bootstrap baseline page sequence. |
| [**downloadLibraryContent**](LibrariesApi.md#downloadlibrarycontent) | **GET** /libraries/content/{grantId} | Redeem one authorized short-lived immutable-content read grant. |
| [**getLibraryCatalog**](LibrariesApi.md#getlibrarycatalog) | **POST** /libraries/catalog/get | Get the persisted Library configuration. |
| [**getLibraryChanges**](LibrariesApi.md#getlibrarychanges) | **POST** /libraries/changes/get | Read repository-ordered accepted Library changes after an opaque cursor. |
| [**getLibraryItem**](LibrariesApi.md#getlibraryitem) | **POST** /libraries/items/get | Get one current Library item. |
| [**getLibraryNamespaceSlot**](LibrariesApi.md#getlibrarynamespaceslot) | **POST** /libraries/namespace/get-slot | Get one current occupied or remembered-vacant Library namespace slot. |
| [**getLibraryOperation**](LibrariesApi.md#getlibraryoperation) | **POST** /libraries/operations/get | Get the stable receipt for one authorized Library operation identity. |
| [**getLibraryStatus**](LibrariesApi.md#getlibrarystatus) | **POST** /libraries/status/get | Get content-free Library repository status. |
| [**listLibraries**](LibrariesApi.md#listlibraries) | **POST** /libraries/list | List the sorted Libraries and their exact configuration version. |
| [**prepareLibraryContent**](LibrariesApi.md#preparelibrarycontent) | **POST** /libraries/content/prepare | Prepare exact immutable bytes for a later Library change. |
| [**prepareLibraryContentRead**](LibrariesApi.md#preparelibrarycontentread) | **POST** /libraries/content/read | Prepare a one-use read grant for an authorized retained content version. |
| [**removeLibrary**](LibrariesApi.md#removelibrary) | **POST** /libraries/remove | Remove one empty normalized Library under an exact configuration version. |
| [**startLibraryBootstrap**](LibrariesApi.md#startlibrarybootstrap) | **POST** /libraries/bootstrap/start | Start a bounded bootstrap from the current immutable baseline. |
| [**submitLibraryChange**](LibrariesApi.md#submitlibrarychange) | **POST** /libraries/changes/submit | Submit one exact idempotent Library namespace or content change. |



## addLibrary

> LibraryCatalogChangeReturnValue addLibrary(addLibraryParameters)

Add one empty normalized Library under an exact configuration version.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { AddLibraryRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // AddLibraryParameters
    addLibraryParameters: ...,
  } satisfies AddLibraryRequest;

  try {
    const data = await api.addLibrary(body);
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
| **addLibraryParameters** | [AddLibraryParameters](AddLibraryParameters.md) |  | |

### Return type

[**LibraryCatalogChangeReturnValue**](LibraryCatalogChangeReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Exact-version Library change result. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **409** | Conflict |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## continueLibraryBootstrap

> LibraryBootstrapPageReturnValue continueLibraryBootstrap(continueLibraryBootstrapParameters)

Continue one immutable bootstrap baseline page sequence.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ContinueLibraryBootstrapRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // ContinueLibraryBootstrapParameters
    continueLibraryBootstrapParameters: ...,
  } satisfies ContinueLibraryBootstrapRequest;

  try {
    const data = await api.continueLibraryBootstrap(body);
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
| **continueLibraryBootstrapParameters** | [ContinueLibraryBootstrapParameters](ContinueLibraryBootstrapParameters.md) |  | |

### Return type

[**LibraryBootstrapPageReturnValue**](LibraryBootstrapPageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | One immutable bootstrap baseline page. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **404** | Not Found |  -  |
| **410** | Gone |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## downloadLibraryContent

> Blob downloadLibraryContent(grantId)

Redeem one authorized short-lived immutable-content read grant.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DownloadLibraryContentRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const api = new LibrariesApi();

  const body = {
    // string
    grantId: grantId_example,
  } satisfies DownloadLibraryContentRequest;

  try {
    const data = await api.downloadLibraryContent(body);
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
| **grantId** | `string` |  | [Defaults to `undefined`] |

### Return type

**Blob**

### Authorization

No authorization required

### HTTP request headers

- **Content-Type**: Not defined
- **Accept**: `application/octet-stream`, `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Exact immutable bytes authorized by the one-use grant. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **404** | Not Found |  -  |
| **410** | Gone |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getLibraryCatalog

> LibraryCatalogReturnValue getLibraryCatalog(getLibraryCatalogParameters)

Get the persisted Library configuration.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetLibraryCatalogRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // GetLibraryCatalogParameters
    getLibraryCatalogParameters: ...,
  } satisfies GetLibraryCatalogRequest;

  try {
    const data = await api.getLibraryCatalog(body);
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
| **getLibraryCatalogParameters** | [GetLibraryCatalogParameters](GetLibraryCatalogParameters.md) |  | |

### Return type

[**LibraryCatalogReturnValue**](LibraryCatalogReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Current persisted Library configuration. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getLibraryChanges

> LibraryChangePageReturnValue getLibraryChanges(getLibraryChangesParameters)

Read repository-ordered accepted Library changes after an opaque cursor.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetLibraryChangesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // GetLibraryChangesParameters
    getLibraryChangesParameters: ...,
  } satisfies GetLibraryChangesRequest;

  try {
    const data = await api.getLibraryChanges(body);
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
| **getLibraryChangesParameters** | [GetLibraryChangesParameters](GetLibraryChangesParameters.md) |  | |

### Return type

[**LibraryChangePageReturnValue**](LibraryChangePageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Ordered accepted changes or a rebaseline instruction. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getLibraryItem

> LibraryItemReturnValue getLibraryItem(getLibraryItemParameters)

Get one current Library item.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetLibraryItemRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // GetLibraryItemParameters
    getLibraryItemParameters: ...,
  } satisfies GetLibraryItemRequest;

  try {
    const data = await api.getLibraryItem(body);
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
| **getLibraryItemParameters** | [GetLibraryItemParameters](GetLibraryItemParameters.md) |  | |

### Return type

[**LibraryItemReturnValue**](LibraryItemReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Current Library item state. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **404** | Not Found |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getLibraryNamespaceSlot

> LibraryNamespaceSlotReturnValue getLibraryNamespaceSlot(getLibraryNamespaceSlotParameters)

Get one current occupied or remembered-vacant Library namespace slot.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetLibraryNamespaceSlotRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // GetLibraryNamespaceSlotParameters
    getLibraryNamespaceSlotParameters: ...,
  } satisfies GetLibraryNamespaceSlotRequest;

  try {
    const data = await api.getLibraryNamespaceSlot(body);
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
| **getLibraryNamespaceSlotParameters** | [GetLibraryNamespaceSlotParameters](GetLibraryNamespaceSlotParameters.md) |  | |

### Return type

[**LibraryNamespaceSlotReturnValue**](LibraryNamespaceSlotReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Current occupied or remembered-vacant namespace slot. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getLibraryOperation

> LibraryOperationReceiptReturnValue getLibraryOperation(getLibraryOperationParameters)

Get the stable receipt for one authorized Library operation identity.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetLibraryOperationRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // GetLibraryOperationParameters
    getLibraryOperationParameters: ...,
  } satisfies GetLibraryOperationRequest;

  try {
    const data = await api.getLibraryOperation(body);
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
| **getLibraryOperationParameters** | [GetLibraryOperationParameters](GetLibraryOperationParameters.md) |  | |

### Return type

[**LibraryOperationReceiptReturnValue**](LibraryOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Stable Library operation receipt. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **404** | Not Found |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getLibraryStatus

> LibraryStatusReturnValue getLibraryStatus(getLibraryStatusParameters)

Get content-free Library repository status.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetLibraryStatusRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // GetLibraryStatusParameters
    getLibraryStatusParameters: ...,
  } satisfies GetLibraryStatusRequest;

  try {
    const data = await api.getLibraryStatus(body);
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
| **getLibraryStatusParameters** | [GetLibraryStatusParameters](GetLibraryStatusParameters.md) |  | |

### Return type

[**LibraryStatusReturnValue**](LibraryStatusReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Content-free Library repository status. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## listLibraries

> LibraryCatalogReturnValue listLibraries(listLibrariesParameters)

List the sorted Libraries and their exact configuration version.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListLibrariesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // ListLibrariesParameters
    listLibrariesParameters: ...,
  } satisfies ListLibrariesRequest;

  try {
    const data = await api.listLibraries(body);
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
| **listLibrariesParameters** | [ListLibrariesParameters](ListLibrariesParameters.md) |  | |

### Return type

[**LibraryCatalogReturnValue**](LibraryCatalogReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Current persisted Library configuration. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## prepareLibraryContent

> LibraryPreparedContentReturnValue prepareLibraryContent(prepareLibraryContentParameters)

Prepare exact immutable bytes for a later Library change.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { PrepareLibraryContentRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // PrepareLibraryContentParameters
    prepareLibraryContentParameters: ...,
  } satisfies PrepareLibraryContentRequest;

  try {
    const data = await api.prepareLibraryContent(body);
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
| **prepareLibraryContentParameters** | [PrepareLibraryContentParameters](PrepareLibraryContentParameters.md) |  | |

### Return type

[**LibraryPreparedContentReturnValue**](LibraryPreparedContentReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Authorized immutable-content preparation. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **409** | Conflict |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## prepareLibraryContentRead

> LibraryContentReadGrantReturnValue prepareLibraryContentRead(prepareLibraryContentReadParameters)

Prepare a one-use read grant for an authorized retained content version.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { PrepareLibraryContentReadRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // PrepareLibraryContentReadParameters
    prepareLibraryContentReadParameters: ...,
  } satisfies PrepareLibraryContentReadRequest;

  try {
    const data = await api.prepareLibraryContentRead(body);
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
| **prepareLibraryContentReadParameters** | [PrepareLibraryContentReadParameters](PrepareLibraryContentReadParameters.md) |  | |

### Return type

[**LibraryContentReadGrantReturnValue**](LibraryContentReadGrantReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | One-use authorized immutable-content read grant. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **404** | Not Found |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## removeLibrary

> LibraryCatalogChangeReturnValue removeLibrary(removeLibraryParameters)

Remove one empty normalized Library under an exact configuration version.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RemoveLibraryRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // RemoveLibraryParameters
    removeLibraryParameters: ...,
  } satisfies RemoveLibraryRequest;

  try {
    const data = await api.removeLibrary(body);
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
| **removeLibraryParameters** | [RemoveLibraryParameters](RemoveLibraryParameters.md) |  | |

### Return type

[**LibraryCatalogChangeReturnValue**](LibraryCatalogChangeReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Exact-version Library change result. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **409** | Conflict |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## startLibraryBootstrap

> LibraryBootstrapPageReturnValue startLibraryBootstrap(startLibraryBootstrapParameters)

Start a bounded bootstrap from the current immutable baseline.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { StartLibraryBootstrapRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // StartLibraryBootstrapParameters
    startLibraryBootstrapParameters: ...,
  } satisfies StartLibraryBootstrapRequest;

  try {
    const data = await api.startLibraryBootstrap(body);
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
| **startLibraryBootstrapParameters** | [StartLibraryBootstrapParameters](StartLibraryBootstrapParameters.md) |  | |

### Return type

[**LibraryBootstrapPageReturnValue**](LibraryBootstrapPageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | One immutable bootstrap baseline page. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## submitLibraryChange

> LibraryOperationReceiptReturnValue submitLibraryChange(submitLibraryChangeParameters)

Submit one exact idempotent Library namespace or content change.

### Example

```ts
import {
  Configuration,
  LibrariesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SubmitLibraryChangeRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new LibrariesApi(config);

  const body = {
    // SubmitLibraryChangeParameters
    submitLibraryChangeParameters: ...,
  } satisfies SubmitLibraryChangeRequest;

  try {
    const data = await api.submitLibraryChange(body);
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
| **submitLibraryChangeParameters** | [SubmitLibraryChangeParameters](SubmitLibraryChangeParameters.md) |  | |

### Return type

[**LibraryOperationReceiptReturnValue**](LibraryOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Stable Library operation receipt. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **409** | Conflict |  -  |
| **410** | Gone |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)

