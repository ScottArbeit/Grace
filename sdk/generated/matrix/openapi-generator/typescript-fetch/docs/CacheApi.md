# CacheApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**assignCacheRepositories**](CacheApi.md#assigncacherepositories) | **POST** /cache/assign-repositories | Replace a Cache\&#39;s exact repository assignments as a current administrator. |
| [**enrollCache**](CacheApi.md#enrollcache) | **POST** /cache/enroll | Enroll a Grace Cache with an administrator-authorized repository boundary. |
| [**getArtifactGrantValidationKeys**](CacheApi.md#getartifactgrantvalidationkeys) | **GET** /cache/validation-keys | Publish artifact grant validation keys. |
| [**refreshCache**](CacheApi.md#refreshcache) | **POST** /cache/refresh | Refresh Cache operational facts with a current identity-key proof. |
| [**revokeCache**](CacheApi.md#revokecache) | **POST** /cache/revoke | Revoke a Cache registration as a current administrator. |
| [**submitCacheKeyCandidate**](CacheApi.md#submitcachekeycandidate) | **POST** /cache/candidate | Submit or reuse one Cache identity candidate after active-key proof. |



## assignCacheRepositories

> CacheRegistrationReturnValue assignCacheRepositories(cacheRepositoryAssignmentRequest)

Replace a Cache\&#39;s exact repository assignments as a current administrator.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { AssignCacheRepositoriesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new CacheApi(config);

  const body = {
    // CacheRepositoryAssignmentRequest
    cacheRepositoryAssignmentRequest: ...,
  } satisfies AssignCacheRepositoriesRequest;

  try {
    const data = await api.assignCacheRepositories(body);
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
| **cacheRepositoryAssignmentRequest** | [CacheRepositoryAssignmentRequest](CacheRepositoryAssignmentRequest.md) |  | |

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
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## enrollCache

> CacheRegistrationReturnValue enrollCache(cacheEnrollmentRequest)

Enroll a Grace Cache with an administrator-authorized repository boundary.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { EnrollCacheRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new CacheApi(config);

  const body = {
    // CacheEnrollmentRequest
    cacheEnrollmentRequest: ...,
  } satisfies EnrollCacheRequest;

  try {
    const data = await api.enrollCache(body);
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
| **cacheEnrollmentRequest** | [CacheEnrollmentRequest](CacheEnrollmentRequest.md) |  | |

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
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getArtifactGrantValidationKeys

> ArtifactGrantValidationKeySet getArtifactGrantValidationKeys()

Publish artifact grant validation keys.

Public verification material for Grace Cache artifact-grant validation. This operation does not require a Grace user session.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetArtifactGrantValidationKeysRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const api = new CacheApi();

  try {
    const data = await api.getArtifactGrantValidationKeys();
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

[**ArtifactGrantValidationKeySet**](ArtifactGrantValidationKeySet.md)

### Authorization

No authorization required

### HTTP request headers

- **Content-Type**: Not defined
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## refreshCache

> CacheRegistrationReturnValue refreshCache(cacheRegistrationRefreshRequest)

Refresh Cache operational facts with a current identity-key proof.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RefreshCacheRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const api = new CacheApi();

  const body = {
    // CacheRegistrationRefreshRequest
    cacheRegistrationRefreshRequest: ...,
  } satisfies RefreshCacheRequest;

  try {
    const data = await api.refreshCache(body);
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

No authorization required

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


## revokeCache

> CacheRegistrationReturnValue revokeCache(cacheRevocationRequest)

Revoke a Cache registration as a current administrator.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RevokeCacheRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new CacheApi(config);

  const body = {
    // CacheRevocationRequest
    cacheRevocationRequest: ...,
  } satisfies RevokeCacheRequest;

  try {
    const data = await api.revokeCache(body);
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
| **cacheRevocationRequest** | [CacheRevocationRequest](CacheRevocationRequest.md) |  | |

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
| **403** | Forbidden |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## submitCacheKeyCandidate

> CacheRegistrationReturnValue submitCacheKeyCandidate(cacheKeyCandidateRequest)

Submit or reuse one Cache identity candidate after active-key proof.

### Example

```ts
import {
  Configuration,
  CacheApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SubmitCacheKeyCandidateRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const api = new CacheApi();

  const body = {
    // CacheKeyCandidateRequest
    cacheKeyCandidateRequest: ...,
  } satisfies SubmitCacheKeyCandidateRequest;

  try {
    const data = await api.submitCacheKeyCandidate(body);
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
| **cacheKeyCandidateRequest** | [CacheKeyCandidateRequest](CacheKeyCandidateRequest.md) |  | |

### Return type

[**CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

No authorization required

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

