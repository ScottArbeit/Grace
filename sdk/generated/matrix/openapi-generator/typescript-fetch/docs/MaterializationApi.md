# MaterializationApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**createMaterializationPlan**](MaterializationApi.md#creatematerializationplan) | **POST** /materialization/plan | Create a Materialization Plan. |



## createMaterializationPlan

> MaterializationPlanReturnValue createMaterializationPlan(planParameters)

Create a Materialization Plan.

Resolves a Materialization Plan request on the server side. This tracer slice supports Direct/Bypass planning for immutable root directory selectors and rejects cache-backed or path-scoped requests until later slices own those behaviors.

### Example

```ts
import {
  Configuration,
  MaterializationApi,
} from '@grace-vcs/generated-openapi-probe';
import type { CreateMaterializationPlanRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new MaterializationApi(config);

  const body = {
    // PlanParameters
    planParameters: {"CorrelationId":"cli-20260707T105500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","Request":{"Class":"MaterializationPlanRequest","TargetSelector":{"Class":"MaterializationTargetSelector","SelectorKind":"directoryVersionId","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","ReferenceId":null,"BranchName":null},"ExecutionMode":"direct","CacheSelection":{"Class":"MaterializationCacheSelection","SelectionKind":"bypassCache"},"RequestedArtifactKinds":["directoryVersionZip","recursiveDirectoryMetadata"]}},
  } satisfies CreateMaterializationPlanRequest;

  try {
    const data = await api.createMaterializationPlan(body);
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
| **planParameters** | [PlanParameters](PlanParameters.md) |  | |

### Return type

[**MaterializationPlanReturnValue**](MaterializationPlanReturnValue.md)

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

