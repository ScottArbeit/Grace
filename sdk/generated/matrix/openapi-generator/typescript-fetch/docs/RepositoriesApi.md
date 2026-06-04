# RepositoriesApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**createRepository**](RepositoriesApi.md#createrepository) | **POST** /repository/create | Create a new repository. |
| [**deleteRepository**](RepositoriesApi.md#deleterepository) | **POST** /repository/delete | Delete a repository. |
| [**getRepository**](RepositoriesApi.md#getrepository) | **POST** /repository/get | Get a repository. |
| [**listRepositoryBranches**](RepositoriesApi.md#listrepositorybranches) | **POST** /repository/getBranches | List repository branches. |
| [**listRepositoryBranchesByBranchId**](RepositoriesApi.md#listrepositorybranchesbybranchid) | **POST** /repository/getBranchesByBranchId | List branches by branch id. |
| [**listRepositoryReferencesByReferenceId**](RepositoriesApi.md#listrepositoryreferencesbyreferenceid) | **POST** /repository/getReferencesByReferenceId | List references by reference id. |
| [**repositoryExists**](RepositoriesApi.md#repositoryexists) | **POST** /repository/exists | Check whether a repository exists. |
| [**repositoryIsEmpty**](RepositoriesApi.md#repositoryisempty) | **POST** /repository/isEmpty | Check whether a repository is empty. |
| [**setRepositoryCheckpointDays**](RepositoriesApi.md#setrepositorycheckpointdays) | **POST** /repository/setCheckpointDays | Set checkpoint retention. |
| [**setRepositoryDefaultServerApiVersion**](RepositoriesApi.md#setrepositorydefaultserverapiversion) | **POST** /repository/setDefaultServerApiVersion | Set the default server API version. |
| [**setRepositoryDescription**](RepositoriesApi.md#setrepositorydescription) | **POST** /repository/setDescription | Set repository description. |
| [**setRepositoryName**](RepositoriesApi.md#setrepositoryname) | **POST** /repository/setName | Set the name of a repository. |
| [**setRepositoryRecordSaves**](RepositoriesApi.md#setrepositoryrecordsaves) | **POST** /repository/setRecordSaves | Set whether saves are recorded. |
| [**setRepositorySaveDays**](RepositoriesApi.md#setrepositorysavedays) | **POST** /repository/setSaveDays | Set save retention. |
| [**setRepositoryStatus**](RepositoriesApi.md#setrepositorystatus) | **POST** /repository/setStatus | Set repository status. |
| [**setRepositoryVisibility**](RepositoriesApi.md#setrepositoryvisibility) | **POST** /repository/setVisibility | Set repository visibility. |
| [**undeleteRepository**](RepositoriesApi.md#undeleterepository) | **POST** /repository/undelete | Undelete a previously deleted repository. |



## createRepository

> RepositoryCommandReturnValue createRepository(createRepositoryParameters)

Create a new repository.

Creates a repository in an organization.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { CreateRepositoryRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // CreateRepositoryParameters
    createRepositoryParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage"},
  } satisfies CreateRepositoryRequest;

  try {
    const data = await api.createRepository(body);
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
| **createRepositoryParameters** | [CreateRepositoryParameters](CreateRepositoryParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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


## deleteRepository

> RepositoryCommandReturnValue deleteRepository(deleteRepositoryParameters)

Delete a repository.

Logically deletes a repository that exists and has not already been deleted.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DeleteRepositoryRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // DeleteRepositoryParameters
    deleteRepositoryParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","Force":false,"DeleteReason":"Repository retired."},
  } satisfies DeleteRepositoryRequest;

  try {
    const data = await api.deleteRepository(body);
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
| **deleteRepositoryParameters** | [DeleteRepositoryParameters](DeleteRepositoryParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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


## getRepository

> RepositoryReturnValue getRepository(repositoryParameters)

Get a repository.

Gets repository metadata.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetRepositoryRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // RepositoryParameters
    repositoryParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage"},
  } satisfies GetRepositoryRequest;

  try {
    const data = await api.getRepository(body);
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
| **repositoryParameters** | [RepositoryParameters](RepositoryParameters.md) |  | |

### Return type

[**RepositoryReturnValue**](RepositoryReturnValue.md)

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


## listRepositoryBranches

> RepositoryBranchesReturnValue listRepositoryBranches(getBranchesParameters)

List repository branches.

Gets branches for the repository.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListRepositoryBranchesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // GetBranchesParameters
    getBranchesParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","IncludeDeleted":false,"MaxCount":50},
  } satisfies ListRepositoryBranchesRequest;

  try {
    const data = await api.listRepositoryBranches(body);
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
| **getBranchesParameters** | [GetBranchesParameters](GetBranchesParameters.md) |  | |

### Return type

[**RepositoryBranchesReturnValue**](RepositoryBranchesReturnValue.md)

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


## listRepositoryBranchesByBranchId

> RepositoryBranchesReturnValue listRepositoryBranchesByBranchId(getBranchesByBranchIdParameters)

List branches by branch id.

Gets branches in the repository by branch ids.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListRepositoryBranchesByBranchIdRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // GetBranchesByBranchIdParameters
    getBranchesByBranchIdParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","IncludeDeleted":false,"MaxCount":50,"BranchIds":["de7bf47d-23ae-4599-af68-68a317ea390d"]},
  } satisfies ListRepositoryBranchesByBranchIdRequest;

  try {
    const data = await api.listRepositoryBranchesByBranchId(body);
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
| **getBranchesByBranchIdParameters** | [GetBranchesByBranchIdParameters](GetBranchesByBranchIdParameters.md) |  | |

### Return type

[**RepositoryBranchesReturnValue**](RepositoryBranchesReturnValue.md)

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


## listRepositoryReferencesByReferenceId

> RepositoryReferencesReturnValue listRepositoryReferencesByReferenceId(getReferencesByReferenceIdParameters)

List references by reference id.

Gets references in the repository by reference ids.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListRepositoryReferencesByReferenceIdRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // GetReferencesByReferenceIdParameters
    getReferencesByReferenceIdParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","ReferenceIds":["c8f9bac8-d489-46c7-917f-b36b7d9efa9a"],"MaxCount":50},
  } satisfies ListRepositoryReferencesByReferenceIdRequest;

  try {
    const data = await api.listRepositoryReferencesByReferenceId(body);
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
| **getReferencesByReferenceIdParameters** | [GetReferencesByReferenceIdParameters](GetReferencesByReferenceIdParameters.md) |  | |

### Return type

[**RepositoryReferencesReturnValue**](RepositoryReferencesReturnValue.md)

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


## repositoryExists

> RepositoryBooleanReturnValue repositoryExists(repositoryParameters)

Check whether a repository exists.

Checks whether a repository exists for the supplied selector values.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RepositoryExistsRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // RepositoryParameters
    repositoryParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage"},
  } satisfies RepositoryExistsRequest;

  try {
    const data = await api.repositoryExists(body);
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
| **repositoryParameters** | [RepositoryParameters](RepositoryParameters.md) |  | |

### Return type

[**RepositoryBooleanReturnValue**](RepositoryBooleanReturnValue.md)

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


## repositoryIsEmpty

> RepositoryBooleanReturnValue repositoryIsEmpty(isEmptyParameters)

Check whether a repository is empty.

Checks whether a repository has no content yet.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RepositoryIsEmptyRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // IsEmptyParameters
    isEmptyParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage"},
  } satisfies RepositoryIsEmptyRequest;

  try {
    const data = await api.repositoryIsEmpty(body);
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
| **isEmptyParameters** | [IsEmptyParameters](IsEmptyParameters.md) |  | |

### Return type

[**RepositoryBooleanReturnValue**](RepositoryBooleanReturnValue.md)

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


## setRepositoryCheckpointDays

> RepositoryCommandReturnValue setRepositoryCheckpointDays(setCheckpointDaysParameters)

Set checkpoint retention.

Sets the number of days to keep checkpoints in the repository.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetRepositoryCheckpointDaysRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // SetCheckpointDaysParameters
    setCheckpointDaysParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","CheckpointDays":90},
  } satisfies SetRepositoryCheckpointDaysRequest;

  try {
    const data = await api.setRepositoryCheckpointDays(body);
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
| **setCheckpointDaysParameters** | [SetCheckpointDaysParameters](SetCheckpointDaysParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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


## setRepositoryDefaultServerApiVersion

> RepositoryCommandReturnValue setRepositoryDefaultServerApiVersion(setDefaultServerApiVersionParameters)

Set the default server API version.

Sets the default server API version clients should use for the repository.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetRepositoryDefaultServerApiVersionRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // SetDefaultServerApiVersionParameters
    setDefaultServerApiVersionParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","DefaultServerApiVersion":"2026-06-04"},
  } satisfies SetRepositoryDefaultServerApiVersionRequest;

  try {
    const data = await api.setRepositoryDefaultServerApiVersion(body);
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
| **setDefaultServerApiVersionParameters** | [SetDefaultServerApiVersionParameters](SetDefaultServerApiVersionParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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


## setRepositoryDescription

> RepositoryCommandReturnValue setRepositoryDescription(setRepositoryDescriptionParameters)

Set repository description.

Sets the repository description.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetRepositoryDescriptionRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // SetRepositoryDescriptionParameters
    setRepositoryDescriptionParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","Description":"Repository for release candidates."},
  } satisfies SetRepositoryDescriptionRequest;

  try {
    const data = await api.setRepositoryDescription(body);
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
| **setRepositoryDescriptionParameters** | [SetRepositoryDescriptionParameters](SetRepositoryDescriptionParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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


## setRepositoryName

> RepositoryCommandReturnValue setRepositoryName(setRepositoryNameParameters)

Set the name of a repository.

Renames a repository that exists and has not been deleted.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetRepositoryNameRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // SetRepositoryNameParameters
    setRepositoryNameParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","NewName":"sample-repository-renamed"},
  } satisfies SetRepositoryNameRequest;

  try {
    const data = await api.setRepositoryName(body);
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
| **setRepositoryNameParameters** | [SetRepositoryNameParameters](SetRepositoryNameParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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


## setRepositoryRecordSaves

> RepositoryCommandReturnValue setRepositoryRecordSaves(recordSavesParameters)

Set whether saves are recorded.

Sets the repository default for whether save references should be kept.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetRepositoryRecordSavesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // RecordSavesParameters
    recordSavesParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","RecordSaves":true},
  } satisfies SetRepositoryRecordSavesRequest;

  try {
    const data = await api.setRepositoryRecordSaves(body);
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
| **recordSavesParameters** | [RecordSavesParameters](RecordSavesParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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


## setRepositorySaveDays

> RepositoryCommandReturnValue setRepositorySaveDays(setSaveDaysParameters)

Set save retention.

Sets the number of days to keep saves in the repository.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetRepositorySaveDaysRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // SetSaveDaysParameters
    setSaveDaysParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","SaveDays":30},
  } satisfies SetRepositorySaveDaysRequest;

  try {
    const data = await api.setRepositorySaveDays(body);
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
| **setSaveDaysParameters** | [SetSaveDaysParameters](SetSaveDaysParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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


## setRepositoryStatus

> RepositoryCommandReturnValue setRepositoryStatus(setRepositoryStatusParameters)

Set repository status.

Sets the repository operational status.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetRepositoryStatusRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // SetRepositoryStatusParameters
    setRepositoryStatusParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","Status":"Active"},
  } satisfies SetRepositoryStatusRequest;

  try {
    const data = await api.setRepositoryStatus(body);
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
| **setRepositoryStatusParameters** | [SetRepositoryStatusParameters](SetRepositoryStatusParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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


## setRepositoryVisibility

> RepositoryCommandReturnValue setRepositoryVisibility(setRepositoryVisibilityParameters)

Set repository visibility.

Sets whether the repository is public or private.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetRepositoryVisibilityRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // SetRepositoryVisibilityParameters
    setRepositoryVisibilityParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","Visibility":"Private"},
  } satisfies SetRepositoryVisibilityRequest;

  try {
    const data = await api.setRepositoryVisibility(body);
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
| **setRepositoryVisibilityParameters** | [SetRepositoryVisibilityParameters](SetRepositoryVisibilityParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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


## undeleteRepository

> RepositoryCommandReturnValue undeleteRepository(undeleteRepositoryParameters)

Undelete a previously deleted repository.

Restores a logically deleted repository.

### Example

```ts
import {
  Configuration,
  RepositoriesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { UndeleteRepositoryRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new RepositoriesApi(config);

  const body = {
    // UndeleteRepositoryParameters
    undeleteRepositoryParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage"},
  } satisfies UndeleteRepositoryRequest;

  try {
    const data = await api.undeleteRepository(body);
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
| **undeleteRepositoryParameters** | [UndeleteRepositoryParameters](UndeleteRepositoryParameters.md) |  | |

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

