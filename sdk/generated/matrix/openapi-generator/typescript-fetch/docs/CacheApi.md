# CacheApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**getCacheArtifactGrantValidationKey**](CacheApi.md#getcacheartifactgrantvalidationkey) | **GET** /cache/artifact-grant-validation-key | Get the current Server process public key for local Cache grant validation. |
| [**prepareDirectoryVersionZip**](CacheApi.md#preparedirectoryversionzip) | **POST** /cache/prepareDirectoryVersionZip | Prepare one Server-approved DirectoryVersion ZIP read and fill. |
| [**redeemDirectoryVersionZipFill**](CacheApi.md#redeemdirectoryversionzipfill) | **POST** /cache/redeemDirectoryVersionZipFill | Redeem one permit and Cache process signature for a read-only ZIP source. |



## getCacheArtifactGrantValidationKey

> CacheArtifactGrantValidationKeyReturnValue getCacheArtifactGrantValidationKey()

Get the current Server process public key for local Cache grant validation.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetCacheArtifactGrantValidationKeyRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const api = new CacheApi();

  try {
    const data = await api.getCacheArtifactGrantValidationKey();
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

[**CacheArtifactGrantValidationKeyReturnValue**](CacheArtifactGrantValidationKeyReturnValue.md)

### Authorization

No authorization required

### HTTP request headers

- **Content-Type**: Not defined
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Current Server process public key and fixed Cache grant validation contract. |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## prepareDirectoryVersionZip

> DirectoryVersionZipPreparation prepareDirectoryVersionZip(prepareDirectoryVersionZipParameters)

Prepare one Server-approved DirectoryVersion ZIP read and fill.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { PrepareDirectoryVersionZipRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new CacheApi(config);

  const body = {
    // PrepareDirectoryVersionZipParameters
    prepareDirectoryVersionZipParameters: ...,
  } satisfies PrepareDirectoryVersionZipRequest;

  try {
    const data = await api.prepareDirectoryVersionZip(body);
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
| **prepareDirectoryVersionZipParameters** | [PrepareDirectoryVersionZipParameters](PrepareDirectoryVersionZipParameters.md) |  | |

### Return type

[**DirectoryVersionZipPreparation**](DirectoryVersionZipPreparation.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Exact immutable artifact with a five-minute read grant and separate fill permit. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## redeemDirectoryVersionZipFill

> DirectoryVersionZipFillSource redeemDirectoryVersionZipFill(redeemDirectoryVersionZipFillParameters)

Redeem one permit and Cache process signature for a read-only ZIP source.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RedeemDirectoryVersionZipFillRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const api = new CacheApi();

  const body = {
    // RedeemDirectoryVersionZipFillParameters
    redeemDirectoryVersionZipFillParameters: ...,
  } satisfies RedeemDirectoryVersionZipFillRequest;

  try {
    const data = await api.redeemDirectoryVersionZipFill(body);
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
| **redeemDirectoryVersionZipFillParameters** | [RedeemDirectoryVersionZipFillParameters](RedeemDirectoryVersionZipFillParameters.md) |  | |

### Return type

[**DirectoryVersionZipFillSource**](DirectoryVersionZipFillSource.md)

### Authorization

No authorization required

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Exact artifact and fresh read-only Blob source. |  -  |
| **400** | Bad Request |  -  |
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)

