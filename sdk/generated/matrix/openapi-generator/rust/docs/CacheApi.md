# \CacheApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**get_artifact_grant_validation_keys**](CacheApi.md#get_artifact_grant_validation_keys) | **GET** /cache/validation-keys | Publish artifact grant validation keys.
[**refresh_cache_service**](CacheApi.md#refresh_cache_service) | **POST** /cache/refresh | Refresh a Grace Cache service registration.
[**register_cache_service**](CacheApi.md#register_cache_service) | **POST** /cache/register | Register a Grace Cache service.



## get_artifact_grant_validation_keys

> models::ArtifactGrantValidationKeySet get_artifact_grant_validation_keys()
Publish artifact grant validation keys.

Returns current and overlap public validation keys that Grace Cache uses to verify signed artifact grants locally. The response contains no private signing material and advertises a 15-minute cache TTL.

### Parameters

This endpoint does not need any parameter.

### Return type

[**models::ArtifactGrantValidationKeySet**](ArtifactGrantValidationKeySet.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: Not defined
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## refresh_cache_service

> models::CacheRegistrationReturnValue refresh_cache_service(cache_registration_refresh_request)
Refresh a Grace Cache service registration.

Refreshes the current registration for the authenticated Cache service after the server-owned refresh-after interval. Refresh preserves the scopes, capabilities, and execution modes approved during registration.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**cache_registration_refresh_request** | [**CacheRegistrationRefreshRequest**](CacheRegistrationRefreshRequest.md) |  | [required] |

### Return type

[**models::CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## register_cache_service

> models::CacheRegistrationReturnValue register_cache_service(cache_registration_request)
Register a Grace Cache service.

Registers or replaces the server-owned state for an approved Grace Cache service. The caller must authenticate with the configured OIDC JWT bearer service identity. Requested scopes and capabilities are persisted only when they are approved by server configuration.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**cache_registration_request** | [**CacheRegistrationRequest**](CacheRegistrationRequest.md) |  | [required] |

### Return type

[**models::CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

