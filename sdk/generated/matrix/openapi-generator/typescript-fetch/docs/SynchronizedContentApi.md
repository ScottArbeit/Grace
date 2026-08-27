# SynchronizedContentApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**addSynchronizedRoot**](SynchronizedContentApi.md#addsynchronizedroot) | **POST** /sync/roots/add | Add one empty normalized synchronized root under an exact configuration version. |
| [**continueSynchronizedBootstrap**](SynchronizedContentApi.md#continuesynchronizedbootstrap) | **POST** /sync/bootstrap/continue | Continue one immutable bootstrap baseline page sequence. |
| [**downloadSynchronizedContent**](SynchronizedContentApi.md#downloadsynchronizedcontent) | **GET** /sync/content/{grantId} | Redeem one authorized short-lived immutable-content read grant. |
| [**getSynchronizedDeltas**](SynchronizedContentApi.md#getsynchronizeddeltas) | **POST** /sync/deltas/get | Read repository-ordered accepted synchronized mutations after an opaque cursor. |
| [**getSynchronizedItem**](SynchronizedContentApi.md#getsynchronizeditem) | **POST** /sync/items/get | Get one current synchronized item. |
| [**getSynchronizedNamespaceSlot**](SynchronizedContentApi.md#getsynchronizednamespaceslot) | **POST** /sync/namespace/get-slot | Get one current occupied or remembered-vacant synchronized namespace slot. |
| [**getSynchronizedOperation**](SynchronizedContentApi.md#getsynchronizedoperation) | **POST** /sync/operations/get | Get the stable receipt for one authorized synchronized operation identity. |
| [**getSynchronizedRootConfiguration**](SynchronizedContentApi.md#getsynchronizedrootconfiguration) | **POST** /sync/roots/get | Get the persisted synchronized-root configuration. |
| [**getSynchronizedStatus**](SynchronizedContentApi.md#getsynchronizedstatus) | **POST** /sync/status/get | Get content-free synchronized repository status. |
| [**listSynchronizedRoots**](SynchronizedContentApi.md#listsynchronizedroots) | **POST** /sync/roots/list | List the sorted synchronized roots and their exact configuration version. |
| [**prepareSynchronizedContent**](SynchronizedContentApi.md#preparesynchronizedcontent) | **POST** /sync/content/prepare | Prepare exact immutable bytes for a later synchronized mutation. |
| [**prepareSynchronizedContentRead**](SynchronizedContentApi.md#preparesynchronizedcontentread) | **POST** /sync/content/read | Prepare a one-use read grant for an authorized retained content version. |
| [**removeSynchronizedRoot**](SynchronizedContentApi.md#removesynchronizedroot) | **POST** /sync/roots/remove | Remove one empty normalized synchronized root under an exact configuration version. |
| [**startSynchronizedBootstrap**](SynchronizedContentApi.md#startsynchronizedbootstrap) | **POST** /sync/bootstrap/start | Start a bounded bootstrap from the current immutable baseline. |
| [**submitSynchronizedMutation**](SynchronizedContentApi.md#submitsynchronizedmutation) | **POST** /sync/mutations/submit | Submit one exact idempotent synchronized namespace or content mutation. |



## addSynchronizedRoot

> SynchronizedRootMutationReturnValue addSynchronizedRoot(addSynchronizedRootParameters)

Add one empty normalized synchronized root under an exact configuration version.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { AddSynchronizedRootRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // AddSynchronizedRootParameters
    addSynchronizedRootParameters: ...,
  } satisfies AddSynchronizedRootRequest;

  try {
    const data = await api.addSynchronizedRoot(body);
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
| **addSynchronizedRootParameters** | [AddSynchronizedRootParameters](AddSynchronizedRootParameters.md) |  | |

### Return type

[**SynchronizedRootMutationReturnValue**](SynchronizedRootMutationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Exact-version synchronized-root mutation result. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **409** | Conflict |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## continueSynchronizedBootstrap

> SynchronizedBootstrapPageReturnValue continueSynchronizedBootstrap(continueSynchronizedBootstrapParameters)

Continue one immutable bootstrap baseline page sequence.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ContinueSynchronizedBootstrapRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // ContinueSynchronizedBootstrapParameters
    continueSynchronizedBootstrapParameters: ...,
  } satisfies ContinueSynchronizedBootstrapRequest;

  try {
    const data = await api.continueSynchronizedBootstrap(body);
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
| **continueSynchronizedBootstrapParameters** | [ContinueSynchronizedBootstrapParameters](ContinueSynchronizedBootstrapParameters.md) |  | |

### Return type

[**SynchronizedBootstrapPageReturnValue**](SynchronizedBootstrapPageReturnValue.md)

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


## downloadSynchronizedContent

> Blob downloadSynchronizedContent(grantId)

Redeem one authorized short-lived immutable-content read grant.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DownloadSynchronizedContentRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const api = new SynchronizedContentApi();

  const body = {
    // string
    grantId: grantId_example,
  } satisfies DownloadSynchronizedContentRequest;

  try {
    const data = await api.downloadSynchronizedContent(body);
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


## getSynchronizedDeltas

> SynchronizedDeltaReturnValue getSynchronizedDeltas(getSynchronizedDeltasParameters)

Read repository-ordered accepted synchronized mutations after an opaque cursor.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetSynchronizedDeltasRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // GetSynchronizedDeltasParameters
    getSynchronizedDeltasParameters: ...,
  } satisfies GetSynchronizedDeltasRequest;

  try {
    const data = await api.getSynchronizedDeltas(body);
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
| **getSynchronizedDeltasParameters** | [GetSynchronizedDeltasParameters](GetSynchronizedDeltasParameters.md) |  | |

### Return type

[**SynchronizedDeltaReturnValue**](SynchronizedDeltaReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Ordered accepted mutations or a rebaseline instruction. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getSynchronizedItem

> SynchronizedItemReturnValue getSynchronizedItem(getSynchronizedItemParameters)

Get one current synchronized item.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetSynchronizedItemRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // GetSynchronizedItemParameters
    getSynchronizedItemParameters: ...,
  } satisfies GetSynchronizedItemRequest;

  try {
    const data = await api.getSynchronizedItem(body);
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
| **getSynchronizedItemParameters** | [GetSynchronizedItemParameters](GetSynchronizedItemParameters.md) |  | |

### Return type

[**SynchronizedItemReturnValue**](SynchronizedItemReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Current synchronized item state. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **404** | Not Found |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getSynchronizedNamespaceSlot

> SynchronizedNamespaceSlotReturnValue getSynchronizedNamespaceSlot(getSynchronizedNamespaceSlotParameters)

Get one current occupied or remembered-vacant synchronized namespace slot.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetSynchronizedNamespaceSlotRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // GetSynchronizedNamespaceSlotParameters
    getSynchronizedNamespaceSlotParameters: ...,
  } satisfies GetSynchronizedNamespaceSlotRequest;

  try {
    const data = await api.getSynchronizedNamespaceSlot(body);
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
| **getSynchronizedNamespaceSlotParameters** | [GetSynchronizedNamespaceSlotParameters](GetSynchronizedNamespaceSlotParameters.md) |  | |

### Return type

[**SynchronizedNamespaceSlotReturnValue**](SynchronizedNamespaceSlotReturnValue.md)

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


## getSynchronizedOperation

> SynchronizedOperationReceiptReturnValue getSynchronizedOperation(getSynchronizedOperationParameters)

Get the stable receipt for one authorized synchronized operation identity.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetSynchronizedOperationRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // GetSynchronizedOperationParameters
    getSynchronizedOperationParameters: ...,
  } satisfies GetSynchronizedOperationRequest;

  try {
    const data = await api.getSynchronizedOperation(body);
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
| **getSynchronizedOperationParameters** | [GetSynchronizedOperationParameters](GetSynchronizedOperationParameters.md) |  | |

### Return type

[**SynchronizedOperationReceiptReturnValue**](SynchronizedOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Stable synchronized operation receipt. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **404** | Not Found |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getSynchronizedRootConfiguration

> SynchronizedRootConfigurationReturnValue getSynchronizedRootConfiguration(getSynchronizedRootConfigurationParameters)

Get the persisted synchronized-root configuration.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetSynchronizedRootConfigurationRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // GetSynchronizedRootConfigurationParameters
    getSynchronizedRootConfigurationParameters: ...,
  } satisfies GetSynchronizedRootConfigurationRequest;

  try {
    const data = await api.getSynchronizedRootConfiguration(body);
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
| **getSynchronizedRootConfigurationParameters** | [GetSynchronizedRootConfigurationParameters](GetSynchronizedRootConfigurationParameters.md) |  | |

### Return type

[**SynchronizedRootConfigurationReturnValue**](SynchronizedRootConfigurationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Current persisted synchronized-root configuration. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getSynchronizedStatus

> SynchronizedStatusReturnValue getSynchronizedStatus(getSynchronizedStatusParameters)

Get content-free synchronized repository status.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetSynchronizedStatusRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // GetSynchronizedStatusParameters
    getSynchronizedStatusParameters: ...,
  } satisfies GetSynchronizedStatusRequest;

  try {
    const data = await api.getSynchronizedStatus(body);
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
| **getSynchronizedStatusParameters** | [GetSynchronizedStatusParameters](GetSynchronizedStatusParameters.md) |  | |

### Return type

[**SynchronizedStatusReturnValue**](SynchronizedStatusReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Content-free synchronized repository status. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## listSynchronizedRoots

> SynchronizedRootConfigurationReturnValue listSynchronizedRoots(listSynchronizedRootsParameters)

List the sorted synchronized roots and their exact configuration version.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListSynchronizedRootsRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // ListSynchronizedRootsParameters
    listSynchronizedRootsParameters: ...,
  } satisfies ListSynchronizedRootsRequest;

  try {
    const data = await api.listSynchronizedRoots(body);
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
| **listSynchronizedRootsParameters** | [ListSynchronizedRootsParameters](ListSynchronizedRootsParameters.md) |  | |

### Return type

[**SynchronizedRootConfigurationReturnValue**](SynchronizedRootConfigurationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Current persisted synchronized-root configuration. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## prepareSynchronizedContent

> SynchronizedPreparedContentReturnValue prepareSynchronizedContent(prepareSynchronizedContentParameters)

Prepare exact immutable bytes for a later synchronized mutation.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { PrepareSynchronizedContentRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // PrepareSynchronizedContentParameters
    prepareSynchronizedContentParameters: ...,
  } satisfies PrepareSynchronizedContentRequest;

  try {
    const data = await api.prepareSynchronizedContent(body);
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
| **prepareSynchronizedContentParameters** | [PrepareSynchronizedContentParameters](PrepareSynchronizedContentParameters.md) |  | |

### Return type

[**SynchronizedPreparedContentReturnValue**](SynchronizedPreparedContentReturnValue.md)

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


## prepareSynchronizedContentRead

> SynchronizedContentReadGrantReturnValue prepareSynchronizedContentRead(prepareSynchronizedContentReadParameters)

Prepare a one-use read grant for an authorized retained content version.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { PrepareSynchronizedContentReadRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // PrepareSynchronizedContentReadParameters
    prepareSynchronizedContentReadParameters: ...,
  } satisfies PrepareSynchronizedContentReadRequest;

  try {
    const data = await api.prepareSynchronizedContentRead(body);
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
| **prepareSynchronizedContentReadParameters** | [PrepareSynchronizedContentReadParameters](PrepareSynchronizedContentReadParameters.md) |  | |

### Return type

[**SynchronizedContentReadGrantReturnValue**](SynchronizedContentReadGrantReturnValue.md)

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


## removeSynchronizedRoot

> SynchronizedRootMutationReturnValue removeSynchronizedRoot(removeSynchronizedRootParameters)

Remove one empty normalized synchronized root under an exact configuration version.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RemoveSynchronizedRootRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // RemoveSynchronizedRootParameters
    removeSynchronizedRootParameters: ...,
  } satisfies RemoveSynchronizedRootRequest;

  try {
    const data = await api.removeSynchronizedRoot(body);
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
| **removeSynchronizedRootParameters** | [RemoveSynchronizedRootParameters](RemoveSynchronizedRootParameters.md) |  | |

### Return type

[**SynchronizedRootMutationReturnValue**](SynchronizedRootMutationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Exact-version synchronized-root mutation result. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **409** | Conflict |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## startSynchronizedBootstrap

> SynchronizedBootstrapPageReturnValue startSynchronizedBootstrap(startSynchronizedBootstrapParameters)

Start a bounded bootstrap from the current immutable baseline.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { StartSynchronizedBootstrapRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // StartSynchronizedBootstrapParameters
    startSynchronizedBootstrapParameters: ...,
  } satisfies StartSynchronizedBootstrapRequest;

  try {
    const data = await api.startSynchronizedBootstrap(body);
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
| **startSynchronizedBootstrapParameters** | [StartSynchronizedBootstrapParameters](StartSynchronizedBootstrapParameters.md) |  | |

### Return type

[**SynchronizedBootstrapPageReturnValue**](SynchronizedBootstrapPageReturnValue.md)

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


## submitSynchronizedMutation

> SynchronizedOperationReceiptReturnValue submitSynchronizedMutation(submitSynchronizedMutationParameters)

Submit one exact idempotent synchronized namespace or content mutation.

### Example

```ts
import {
  Configuration,
  SynchronizedContentApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SubmitSynchronizedMutationRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new SynchronizedContentApi(config);

  const body = {
    // SubmitSynchronizedMutationParameters
    submitSynchronizedMutationParameters: ...,
  } satisfies SubmitSynchronizedMutationRequest;

  try {
    const data = await api.submitSynchronizedMutation(body);
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
| **submitSynchronizedMutationParameters** | [SubmitSynchronizedMutationParameters](SubmitSynchronizedMutationParameters.md) |  | |

### Return type

[**SynchronizedOperationReceiptReturnValue**](SynchronizedOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Stable synchronized operation receipt. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **409** | Conflict |  -  |
| **410** | Gone |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)

