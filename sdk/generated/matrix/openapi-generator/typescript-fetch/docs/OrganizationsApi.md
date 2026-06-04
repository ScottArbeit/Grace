# OrganizationsApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**createOrganization**](OrganizationsApi.md#createorganization) | **POST** /organization/create | Create an organization. |
| [**deleteOrganization**](OrganizationsApi.md#deleteorganization) | **POST** /organization/delete | Delete an organization. |
| [**getOrganization**](OrganizationsApi.md#getorganization) | **POST** /organization/get | Get an organization. |
| [**listOrganizationRepositories**](OrganizationsApi.md#listorganizationrepositories) | **POST** /organization/listRepositories | List repositories of an organization. |
| [**setOrganizationDescription**](OrganizationsApi.md#setorganizationdescription) | **POST** /organization/setDescription | Set the organization\&#39;s description. |
| [**setOrganizationName**](OrganizationsApi.md#setorganizationname) | **POST** /organization/setName | Set the name of an organization. |
| [**setOrganizationSearchVisibility**](OrganizationsApi.md#setorganizationsearchvisibility) | **POST** /organization/setSearchVisibility | Set organization search visibility. |
| [**setOrganizationType**](OrganizationsApi.md#setorganizationtype) | **POST** /organization/setType | Set the organization type. |
| [**undeleteOrganization**](OrganizationsApi.md#undeleteorganization) | **POST** /organization/undelete | Undelete a previously deleted organization. |



## createOrganization

> OrganizationCommandReturnValue createOrganization(createOrganizationParameters)

Create an organization.

Creates an organization under an owner.

### Example

```ts
import {
  Configuration,
  OrganizationsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { CreateOrganizationRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OrganizationsApi(config);

  const body = {
    // CreateOrganizationParameters
    createOrganizationParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org"},
  } satisfies CreateOrganizationRequest;

  try {
    const data = await api.createOrganization(body);
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
| **createOrganizationParameters** | [CreateOrganizationParameters](CreateOrganizationParameters.md) |  | |

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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


## deleteOrganization

> OrganizationCommandReturnValue deleteOrganization(deleteOrganizationParameters)

Delete an organization.

Logically deletes an organization that exists and has not already been deleted.

### Example

```ts
import {
  Configuration,
  OrganizationsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DeleteOrganizationRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OrganizationsApi(config);

  const body = {
    // DeleteOrganizationParameters
    deleteOrganizationParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","Force":false,"DeleteReason":"Organization retired."},
  } satisfies DeleteOrganizationRequest;

  try {
    const data = await api.deleteOrganization(body);
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
| **deleteOrganizationParameters** | [DeleteOrganizationParameters](DeleteOrganizationParameters.md) |  | |

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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


## getOrganization

> OrganizationReturnValue getOrganization(getOrganizationParameters)

Get an organization.

Gets organization metadata.

### Example

```ts
import {
  Configuration,
  OrganizationsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetOrganizationRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OrganizationsApi(config);

  const body = {
    // GetOrganizationParameters
    getOrganizationParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","IncludeDeleted":false},
  } satisfies GetOrganizationRequest;

  try {
    const data = await api.getOrganization(body);
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
| **getOrganizationParameters** | [GetOrganizationParameters](GetOrganizationParameters.md) |  | |

### Return type

[**OrganizationReturnValue**](OrganizationReturnValue.md)

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


## listOrganizationRepositories

> { [key: string]: string; } listOrganizationRepositories(listRepositoriesParameters)

List repositories of an organization.

Lists repositories associated with the organization.

### Example

```ts
import {
  Configuration,
  OrganizationsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListOrganizationRepositoriesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OrganizationsApi(config);

  const body = {
    // ListRepositoriesParameters
    listRepositoriesParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org"},
  } satisfies ListOrganizationRepositoriesRequest;

  try {
    const data = await api.listOrganizationRepositories(body);
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
| **listRepositoriesParameters** | [ListRepositoriesParameters](ListRepositoriesParameters.md) |  | |

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


## setOrganizationDescription

> OrganizationCommandReturnValue setOrganizationDescription(setOrganizationDescriptionParameters)

Set the organization\&#39;s description.

Sets the organization description.

### Example

```ts
import {
  Configuration,
  OrganizationsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetOrganizationDescriptionRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OrganizationsApi(config);

  const body = {
    // SetOrganizationDescriptionParameters
    setOrganizationDescriptionParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","Description":"Engineering organization for Alice's projects."},
  } satisfies SetOrganizationDescriptionRequest;

  try {
    const data = await api.setOrganizationDescription(body);
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
| **setOrganizationDescriptionParameters** | [SetOrganizationDescriptionParameters](SetOrganizationDescriptionParameters.md) |  | |

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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


## setOrganizationName

> OrganizationCommandReturnValue setOrganizationName(setOrganizationNameParameters)

Set the name of an organization.

Renames an organization that exists and has not been deleted.

### Example

```ts
import {
  Configuration,
  OrganizationsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetOrganizationNameRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OrganizationsApi(config);

  const body = {
    // SetOrganizationNameParameters
    setOrganizationNameParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","NewName":"alice-platform"},
  } satisfies SetOrganizationNameRequest;

  try {
    const data = await api.setOrganizationName(body);
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
| **setOrganizationNameParameters** | [SetOrganizationNameParameters](SetOrganizationNameParameters.md) |  | |

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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


## setOrganizationSearchVisibility

> OrganizationCommandReturnValue setOrganizationSearchVisibility(setOrganizationSearchVisibilityParameters)

Set organization search visibility.

Sets whether the organization appears in search results.

### Example

```ts
import {
  Configuration,
  OrganizationsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetOrganizationSearchVisibilityRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OrganizationsApi(config);

  const body = {
    // SetOrganizationSearchVisibilityParameters
    setOrganizationSearchVisibilityParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","SearchVisibility":"Visible"},
  } satisfies SetOrganizationSearchVisibilityRequest;

  try {
    const data = await api.setOrganizationSearchVisibility(body);
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
| **setOrganizationSearchVisibilityParameters** | [SetOrganizationSearchVisibilityParameters](SetOrganizationSearchVisibilityParameters.md) |  | |

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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


## setOrganizationType

> OrganizationCommandReturnValue setOrganizationType(setOrganizationTypeParameters)

Set the organization type.

Sets the organization type to a public or private value accepted by the server.

### Example

```ts
import {
  Configuration,
  OrganizationsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetOrganizationTypeRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OrganizationsApi(config);

  const body = {
    // SetOrganizationTypeParameters
    setOrganizationTypeParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","OrganizationType":"Public"},
  } satisfies SetOrganizationTypeRequest;

  try {
    const data = await api.setOrganizationType(body);
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
| **setOrganizationTypeParameters** | [SetOrganizationTypeParameters](SetOrganizationTypeParameters.md) |  | |

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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


## undeleteOrganization

> OrganizationCommandReturnValue undeleteOrganization(undeleteOrganizationParameters)

Undelete a previously deleted organization.

Restores a logically deleted organization.

### Example

```ts
import {
  Configuration,
  OrganizationsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { UndeleteOrganizationRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OrganizationsApi(config);

  const body = {
    // UndeleteOrganizationParameters
    undeleteOrganizationParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org"},
  } satisfies UndeleteOrganizationRequest;

  try {
    const data = await api.undeleteOrganization(body);
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
| **undeleteOrganizationParameters** | [UndeleteOrganizationParameters](UndeleteOrganizationParameters.md) |  | |

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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

