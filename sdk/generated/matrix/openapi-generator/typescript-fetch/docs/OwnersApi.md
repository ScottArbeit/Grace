# OwnersApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**createOwner**](OwnersApi.md#createowner) | **POST** /owner/create | Create an owner. |
| [**deleteOwner**](OwnersApi.md#deleteowner) | **POST** /owner/delete | Delete an owner. |
| [**getOwner**](OwnersApi.md#getowner) | **POST** /owner/get | Get an owner. |
| [**listOwnerOrganizations**](OwnersApi.md#listownerorganizations) | **POST** /owner/listOrganizations | List the organizations for an owner. |
| [**setOwnerDescription**](OwnersApi.md#setownerdescription) | **POST** /owner/setDescription | Set the owner\&#39;s description. |
| [**setOwnerName**](OwnersApi.md#setownername) | **POST** /owner/setName | Set the name of an owner. |
| [**setOwnerSearchVisibility**](OwnersApi.md#setownersearchvisibility) | **POST** /owner/setSearchVisibility | Set owner search visibility. |
| [**setOwnerType**](OwnersApi.md#setownertype) | **POST** /owner/setType | Set the owner type. |
| [**undeleteOwner**](OwnersApi.md#undeleteowner) | **POST** /owner/undelete | Undelete a previously deleted owner. |



## createOwner

> OwnerCommandReturnValue createOwner(createOwnerParameters)

Create an owner.

Creates an owner with the specified id and name.

### Example

```ts
import {
  Configuration,
  OwnersApi,
} from '@grace-vcs/generated-openapi-probe';
import type { CreateOwnerRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OwnersApi(config);

  const body = {
    // CreateOwnerParameters
    createOwnerParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice"},
  } satisfies CreateOwnerRequest;

  try {
    const data = await api.createOwner(body);
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
| **createOwnerParameters** | [CreateOwnerParameters](CreateOwnerParameters.md) |  | |

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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


## deleteOwner

> OwnerCommandReturnValue deleteOwner(deleteOwnerParameters)

Delete an owner.

Logically deletes an owner that exists and has not already been deleted.

### Example

```ts
import {
  Configuration,
  OwnersApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DeleteOwnerRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OwnersApi(config);

  const body = {
    // DeleteOwnerParameters
    deleteOwnerParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","Force":false,"DeleteReason":"Owner retired."},
  } satisfies DeleteOwnerRequest;

  try {
    const data = await api.deleteOwner(body);
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
| **deleteOwnerParameters** | [DeleteOwnerParameters](DeleteOwnerParameters.md) |  | |

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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


## getOwner

> OwnerReturnValue getOwner(getOwnerParameters)

Get an owner.

Gets owner metadata.

### Example

```ts
import {
  Configuration,
  OwnersApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetOwnerRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OwnersApi(config);

  const body = {
    // GetOwnerParameters
    getOwnerParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","IncludeDeleted":false},
  } satisfies GetOwnerRequest;

  try {
    const data = await api.getOwner(body);
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
| **getOwnerParameters** | [GetOwnerParameters](GetOwnerParameters.md) |  | |

### Return type

[**OwnerReturnValue**](OwnerReturnValue.md)

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


## listOwnerOrganizations

> { [key: string]: string; } listOwnerOrganizations(listOrganizationsParameters)

List the organizations for an owner.

Lists organizations associated with the owner.

### Example

```ts
import {
  Configuration,
  OwnersApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListOwnerOrganizationsRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OwnersApi(config);

  const body = {
    // ListOrganizationsParameters
    listOrganizationsParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice"},
  } satisfies ListOwnerOrganizationsRequest;

  try {
    const data = await api.listOwnerOrganizations(body);
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
| **listOrganizationsParameters** | [ListOrganizationsParameters](ListOrganizationsParameters.md) |  | |

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


## setOwnerDescription

> OwnerCommandReturnValue setOwnerDescription(setOwnerDescriptionParameters)

Set the owner\&#39;s description.

Sets the owner description.

### Example

```ts
import {
  Configuration,
  OwnersApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetOwnerDescriptionRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OwnersApi(config);

  const body = {
    // SetOwnerDescriptionParameters
    setOwnerDescriptionParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","Description":"Source control owner for Alice's projects."},
  } satisfies SetOwnerDescriptionRequest;

  try {
    const data = await api.setOwnerDescription(body);
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
| **setOwnerDescriptionParameters** | [SetOwnerDescriptionParameters](SetOwnerDescriptionParameters.md) |  | |

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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


## setOwnerName

> OwnerCommandReturnValue setOwnerName(setOwnerNameParameters)

Set the name of an owner.

Renames an owner that exists and has not been deleted.

### Example

```ts
import {
  Configuration,
  OwnersApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetOwnerNameRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OwnersApi(config);

  const body = {
    // SetOwnerNameParameters
    setOwnerNameParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","NewName":"alice-renamed"},
  } satisfies SetOwnerNameRequest;

  try {
    const data = await api.setOwnerName(body);
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
| **setOwnerNameParameters** | [SetOwnerNameParameters](SetOwnerNameParameters.md) |  | |

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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


## setOwnerSearchVisibility

> OwnerCommandReturnValue setOwnerSearchVisibility(setOwnerSearchVisibilityParameters)

Set owner search visibility.

Sets whether the owner appears in search results.

### Example

```ts
import {
  Configuration,
  OwnersApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetOwnerSearchVisibilityRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OwnersApi(config);

  const body = {
    // SetOwnerSearchVisibilityParameters
    setOwnerSearchVisibilityParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","SearchVisibility":"Visible"},
  } satisfies SetOwnerSearchVisibilityRequest;

  try {
    const data = await api.setOwnerSearchVisibility(body);
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
| **setOwnerSearchVisibilityParameters** | [SetOwnerSearchVisibilityParameters](SetOwnerSearchVisibilityParameters.md) |  | |

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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


## setOwnerType

> OwnerCommandReturnValue setOwnerType(setOwnerTypeParameters)

Set the owner type.

Sets the owner type to a public or private value accepted by the server.

### Example

```ts
import {
  Configuration,
  OwnersApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SetOwnerTypeRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OwnersApi(config);

  const body = {
    // SetOwnerTypeParameters
    setOwnerTypeParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","OwnerType":"Public"},
  } satisfies SetOwnerTypeRequest;

  try {
    const data = await api.setOwnerType(body);
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
| **setOwnerTypeParameters** | [SetOwnerTypeParameters](SetOwnerTypeParameters.md) |  | |

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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


## undeleteOwner

> OwnerCommandReturnValue undeleteOwner(undeleteOwnerParameters)

Undelete a previously deleted owner.

Restores a logically deleted owner.

### Example

```ts
import {
  Configuration,
  OwnersApi,
} from '@grace-vcs/generated-openapi-probe';
import type { UndeleteOwnerRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new OwnersApi(config);

  const body = {
    // UndeleteOwnerParameters
    undeleteOwnerParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice"},
  } satisfies UndeleteOwnerRequest;

  try {
    const data = await api.undeleteOwner(body);
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
| **undeleteOwnerParameters** | [UndeleteOwnerParameters](UndeleteOwnerParameters.md) |  | |

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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

