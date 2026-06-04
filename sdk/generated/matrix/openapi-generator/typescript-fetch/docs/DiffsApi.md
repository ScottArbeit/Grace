# DiffsApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**getDiff**](DiffsApi.md#getdiff) | **POST** /diff/getDiff | Get a diff. |
| [**getDiffBySha256Hash**](DiffsApi.md#getdiffbysha256hash) | **POST** /diff/getDiffBySha256Hash | Get a diff by SHA-256 hash. |
| [**populateDiff**](DiffsApi.md#populatediff) | **POST** /diff/populate | Populate a diff actor. |



## getDiff

> DiffReturnValue getDiff(getDiffParameters)

Get a diff.

Retrieves the contents of the diff between two directory versions.

### Example

```ts
import {
  Configuration,
  DiffsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetDiffRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DiffsApi(config);

  const body = {
    // GetDiffParameters
    getDiffParameters: {CorrelationId=cli-20260604T181500Z-0001, Principal=user:alice, OwnerId=9dd5f81f-dc43-4839-9173-85d09394f30f, OrganizationId=e35d64a9-b990-44f5-bf02-32ad7d15630c, RepositoryId=ab6f35ef-6e01-440b-8f9b-c343a5272095, DirectoryVersionId1=33a4e36b-828f-4fae-9343-50b6560dc842, DirectoryVersionId2=66b7b8c2-8d2f-4f04-951c-6b3486c4e5d1},
  } satisfies GetDiffRequest;

  try {
    const data = await api.getDiff(body);
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
| **getDiffParameters** | [GetDiffParameters](GetDiffParameters.md) |  | |

### Return type

[**DiffReturnValue**](DiffReturnValue.md)

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


## getDiffBySha256Hash

> DiffReturnValue getDiffBySha256Hash(getDiffBySha256HashParameters)

Get a diff by SHA-256 hash.

Retrieves a diff by comparing two directory versions identified by SHA-256 hash.

### Example

```ts
import {
  Configuration,
  DiffsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetDiffBySha256HashRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DiffsApi(config);

  const body = {
    // GetDiffBySha256HashParameters
    getDiffBySha256HashParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId1":"33a4e36b-828f-4fae-9343-50b6560dc842","DirectoryVersionId2":"66b7b8c2-8d2f-4f04-951c-6b3486c4e5d1","Sha256Hash1":"805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243","Sha256Hash2":"A05331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857240"},
  } satisfies GetDiffBySha256HashRequest;

  try {
    const data = await api.getDiffBySha256Hash(body);
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
| **getDiffBySha256HashParameters** | [GetDiffBySha256HashParameters](GetDiffBySha256HashParameters.md) |  | |

### Return type

[**DiffReturnValue**](DiffReturnValue.md)

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


## populateDiff

> DiffPopulateReturnValue populateDiff(populateParameters)

Populate a diff actor.

Populates the diff actor without returning the diff. This endpoint is meant to be used when generating the diff through reacting to an event. 

### Example

```ts
import {
  Configuration,
  DiffsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { PopulateDiffRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DiffsApi(config);

  const body = {
    // PopulateParameters
    populateParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId1":"33a4e36b-828f-4fae-9343-50b6560dc842","DirectoryVersionId2":"66b7b8c2-8d2f-4f04-951c-6b3486c4e5d1"},
  } satisfies PopulateDiffRequest;

  try {
    const data = await api.populateDiff(body);
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
| **populateParameters** | [PopulateParameters](PopulateParameters.md) |  | |

### Return type

[**DiffPopulateReturnValue**](DiffPopulateReturnValue.md)

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

