# CacheApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**refreshCacheService**](CacheApi.md#refreshcacheservice) | **POST** /cache/refresh | Refresh a Grace Cache service registration. |
| [**registerCacheService**](CacheApi.md#registercacheservice) | **POST** /cache/register | Register a Grace Cache service. |



## refreshCacheService

> CacheRegistrationReturnValue refreshCacheService(cacheRegistrationRefreshRequest)

Refresh a Grace Cache service registration.

Refreshes the current registration for the authenticated Cache service after the server-owned refresh-after interval. Refresh preserves the scopes, capabilities, and execution modes approved during registration.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RefreshCacheServiceRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new CacheApi(config);

  const body = {
    // CacheRegistrationRefreshRequest
    cacheRegistrationRefreshRequest: ...,
  } satisfies RefreshCacheServiceRequest;

  try {
    const data = await api.refreshCacheService(body);
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
| **cacheRegistrationRefreshRequest** | [CacheRegistrationRefreshRequest](CacheRegistrationRefreshRequest.md) |  | |

### Return type

[**CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **401** | Unauthorized |  * WWW-Authenticate - Authentication challenge emitted by the configured ASP.NET Core authentication handler. <br>  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## registerCacheService

> CacheRegistrationReturnValue registerCacheService(cacheRegistrationRequest)

Register a Grace Cache service.

Registers or replaces the server-owned state for an approved Grace Cache service. The caller must authenticate with the configured OIDC JWT bearer service identity. Requested scopes and capabilities are persisted only when they are approved by server configuration.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RegisterCacheServiceRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new CacheApi(config);

  const body = {
    // CacheRegistrationRequest
    cacheRegistrationRequest: ...,
  } satisfies RegisterCacheServiceRequest;

  try {
    const data = await api.registerCacheService(body);
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
| **cacheRegistrationRequest** | [CacheRegistrationRequest](CacheRegistrationRequest.md) |  | |

### Return type

[**CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`, `text/plain`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **401** | Unauthorized |  * WWW-Authenticate - Authentication challenge emitted by the configured ASP.NET Core authentication handler. <br>  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)

