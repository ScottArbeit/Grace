# \MaterializationApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_materialization_plan**](MaterializationApi.md#create_materialization_plan) | **POST** /materialization/plan | Create a Materialization Plan.



## create_materialization_plan

> models::MaterializationPlanReturnValue create_materialization_plan(plan_parameters)
Create a Materialization Plan.

Resolves a Materialization Plan request on the server side. This tracer slice supports Direct/Bypass planning for immutable root directory selectors. CachePreferred atomically falls back to Direct when a cache attempt fails. CacheRequired returns a 503 Grace error with `Properties.Code = cacheRequiredUnavailable` when no eligible Cache or grant capacity is available, and never falls back to Direct.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**plan_parameters** | [**PlanParameters**](PlanParameters.md) |  | [required] |

### Return type

[**models::MaterializationPlanReturnValue**](MaterializationPlanReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

