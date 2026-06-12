# DirectoriesApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**createDirectoryVersion**](DirectoriesApi.md#createdirectoryversion) | **POST** /directory/create | Create a new directory version. |
| [**getDirectoryVersion**](DirectoriesApi.md#getdirectoryversion) | **POST** /directory/get | Get a directory version. |
| [**getDirectoryVersionByBlake3Hash**](DirectoriesApi.md#getdirectoryversionbyblake3hash) | **POST** /directory/getByBlake3Hash | Get a directory version by BLAKE3 hash. |
| [**getDirectoryVersionBySha256Hash**](DirectoriesApi.md#getdirectoryversionbysha256hash) | **POST** /directory/getBySha256Hash | Get a directory version by SHA-256 hash. |
| [**listDirectoryVersionsById**](DirectoriesApi.md#listdirectoryversionsbyid) | **POST** /directory/getByDirectoryIds | List directory versions by id. |
| [**listDirectoryVersionsRecursive**](DirectoriesApi.md#listdirectoryversionsrecursive) | **POST** /directory/getDirectoryVersionsRecursive | List a directory version and its children. |
| [**saveDirectoryVersions**](DirectoriesApi.md#savedirectoryversions) | **POST** /directory/saveDirectoryVersions | Save directory versions. |



## createDirectoryVersion

> DirectoryCommandReturnValue createDirectoryVersion(createParameters)

Create a new directory version.

Creates a directory version for the repository.

### Example

```ts
import {
  Configuration,
  DirectoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { CreateDirectoryVersionRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DirectoriesApi(config);

  const body = {
    // CreateParameters
    createParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","DirectoryVersion":{"Class":"DirectoryVersion","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RelativePath":"src","Sha256Hash":"805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243","Blake3Hash":"9A35D91B2F631BE9025DE753139B88F7B1E71385C412BC3986FF2F38F230841D","Directories":[],"Files":[],"Size":0,"CreatedAt":"2026-06-04T18:15:00Z","HashesValidated":true}},
  } satisfies CreateDirectoryVersionRequest;

  try {
    const data = await api.createDirectoryVersion(body);
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
| **createParameters** | [CreateParameters](CreateParameters.md) |  | |

### Return type

[**DirectoryCommandReturnValue**](DirectoryCommandReturnValue.md)

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


## getDirectoryVersion

> DirectoryVersionReturnValue getDirectoryVersion(getParameters)

Get a directory version.

Gets a directory version DTO.

### Example

```ts
import {
  Configuration,
  DirectoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetDirectoryVersionRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DirectoriesApi(config);

  const body = {
    // GetParameters
    getParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842"},
  } satisfies GetDirectoryVersionRequest;

  try {
    const data = await api.getDirectoryVersion(body);
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
| **getParameters** | [GetParameters](GetParameters.md) |  | |

### Return type

[**DirectoryVersionReturnValue**](DirectoryVersionReturnValue.md)

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


## getDirectoryVersionByBlake3Hash

> DirectoryVersionHashLookupReturnValue getDirectoryVersionByBlake3Hash(getByBlake3HashParameters)

Get a directory version by BLAKE3 hash.

Gets a directory version DTO by BLAKE3 hash or unique BLAKE3 prefix.

### Example

```ts
import {
  Configuration,
  DirectoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetDirectoryVersionByBlake3HashRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DirectoriesApi(config);

  const body = {
    // GetByBlake3HashParameters
    getByBlake3HashParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Blake3Hash":"9A35D91B2F631BE9025DE753139B88F7B1E71385C412BC3986FF2F38F230841D"},
  } satisfies GetDirectoryVersionByBlake3HashRequest;

  try {
    const data = await api.getDirectoryVersionByBlake3Hash(body);
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
| **getByBlake3HashParameters** | [GetByBlake3HashParameters](GetByBlake3HashParameters.md) |  | |

### Return type

[**DirectoryVersionHashLookupReturnValue**](DirectoryVersionHashLookupReturnValue.md)

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


## getDirectoryVersionBySha256Hash

> DirectoryVersionHashLookupReturnValue getDirectoryVersionBySha256Hash(getBySha256HashParameters)

Get a directory version by SHA-256 hash.

Gets a directory version DTO by SHA-256 hash.

### Example

```ts
import {
  Configuration,
  DirectoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetDirectoryVersionBySha256HashRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DirectoriesApi(config);

  const body = {
    // GetBySha256HashParameters
    getBySha256HashParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243"},
  } satisfies GetDirectoryVersionBySha256HashRequest;

  try {
    const data = await api.getDirectoryVersionBySha256Hash(body);
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
| **getBySha256HashParameters** | [GetBySha256HashParameters](GetBySha256HashParameters.md) |  | |

### Return type

[**DirectoryVersionHashLookupReturnValue**](DirectoryVersionHashLookupReturnValue.md)

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


## listDirectoryVersionsById

> DirectoryVersionListReturnValue listDirectoryVersionsById(getByDirectoryIdsParameters)

List directory versions by id.

Gets directory version DTOs by directory version ids.

### Example

```ts
import {
  Configuration,
  DirectoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListDirectoryVersionsByIdRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DirectoriesApi(config);

  const body = {
    // GetByDirectoryIdsParameters
    getByDirectoryIdsParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","DirectoryIds":["33a4e36b-828f-4fae-9343-50b6560dc842"]},
  } satisfies ListDirectoryVersionsByIdRequest;

  try {
    const data = await api.listDirectoryVersionsById(body);
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
| **getByDirectoryIdsParameters** | [GetByDirectoryIdsParameters](GetByDirectoryIdsParameters.md) |  | |

### Return type

[**DirectoryVersionListReturnValue**](DirectoryVersionListReturnValue.md)

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


## listDirectoryVersionsRecursive

> DirectoryVersionListReturnValue listDirectoryVersionsRecursive(getParameters)

List a directory version and its children.

Gets a directory version and all recursive child directory versions.

### Example

```ts
import {
  Configuration,
  DirectoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListDirectoryVersionsRecursiveRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DirectoriesApi(config);

  const body = {
    // GetParameters
    getParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842"},
  } satisfies ListDirectoryVersionsRecursiveRequest;

  try {
    const data = await api.listDirectoryVersionsRecursive(body);
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
| **getParameters** | [GetParameters](GetParameters.md) |  | |

### Return type

[**DirectoryVersionListReturnValue**](DirectoryVersionListReturnValue.md)

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


## saveDirectoryVersions

> DirectoryCommandReturnValue saveDirectoryVersions(saveDirectoryVersionsParameters)

Save directory versions.

Saves a list of directory versions.

### Example

```ts
import {
  Configuration,
  DirectoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SaveDirectoryVersionsRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new DirectoriesApi(config);

  const body = {
    // SaveDirectoryVersionsParameters
    saveDirectoryVersionsParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","DirectoryVersions":[{"Class":"DirectoryVersion","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RelativePath":"src","Sha256Hash":"805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243","Blake3Hash":"9A35D91B2F631BE9025DE753139B88F7B1E71385C412BC3986FF2F38F230841D","Directories":[],"Files":[],"Size":0,"CreatedAt":"2026-06-04T18:15:00Z","HashesValidated":true}]},
  } satisfies SaveDirectoryVersionsRequest;

  try {
    const data = await api.saveDirectoryVersions(body);
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
| **saveDirectoryVersionsParameters** | [SaveDirectoryVersionsParameters](SaveDirectoryVersionsParameters.md) |  | |

### Return type

[**DirectoryCommandReturnValue**](DirectoryCommandReturnValue.md)

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

