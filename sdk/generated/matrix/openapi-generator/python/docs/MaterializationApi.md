# grace_generated_openapi_probe.MaterializationApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_materialization_plan**](MaterializationApi.md#create_materialization_plan) | **POST** /materialization/plan | Create a Materialization Plan.


# **create_materialization_plan**
> MaterializationPlanReturnValue create_materialization_plan(plan_parameters)

Create a Materialization Plan.

Resolves a Materialization Plan request on the server side. This tracer slice supports Direct/Bypass planning for immutable root directory selectors. CachePreferred atomically falls back to Direct when a cache attempt fails. CacheRequired returns a 503 Grace error with `Properties.Code = cacheRequiredUnavailable` when no eligible Cache or grant capacity is available, and never falls back to Direct.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.materialization_plan_return_value import MaterializationPlanReturnValue
from grace_generated_openapi_probe.models.plan_parameters import PlanParameters
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.MaterializationApi(api_client)
    plan_parameters = {"CorrelationId":"cli-20260707T105500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","Request":{"Class":"MaterializationPlanRequest","TargetSelector":{"Class":"MaterializationTargetSelector","SelectorKind":"directoryVersionId","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","ReferenceId":null,"BranchName":null},"ExecutionMode":"direct","CacheSelection":{"Class":"MaterializationCacheSelection","SelectionKind":"bypassCache"},"RequestedArtifactKinds":["directoryVersionZip","recursiveDirectoryMetadata"]}} # PlanParameters | 

    try:
        # Create a Materialization Plan.
        api_response = api_instance.create_materialization_plan(plan_parameters)
        print("The response of MaterializationApi->create_materialization_plan:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling MaterializationApi->create_materialization_plan: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **plan_parameters** | [**PlanParameters**](PlanParameters.md)|  | 

### Return type

[**MaterializationPlanReturnValue**](MaterializationPlanReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |
**503** | Service Unavailable |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

