# grace_generated_openapi_probe.CacheApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**get_artifact_grant_validation_keys**](CacheApi.md#get_artifact_grant_validation_keys) | **GET** /cache/validation-keys | Publish artifact grant validation keys.
[**refresh_cache_service**](CacheApi.md#refresh_cache_service) | **POST** /cache/refresh | Refresh a Grace Cache service registration.
[**register_cache_service**](CacheApi.md#register_cache_service) | **POST** /cache/register | Register a Grace Cache service.


# **get_artifact_grant_validation_keys**
> ArtifactGrantValidationKeySet get_artifact_grant_validation_keys()

Publish artifact grant validation keys.

Returns current and overlap public validation keys that Grace Cache uses to verify signed artifact grants locally. One deployment-wide durable Orleans actor owns the keys used by every Grace Server instance. The response contains no private signing material and advertises a 15-minute cache TTL.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.artifact_grant_validation_key_set import ArtifactGrantValidationKeySet
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
    api_instance = grace_generated_openapi_probe.CacheApi(api_client)

    try:
        # Publish artifact grant validation keys.
        api_response = api_instance.get_artifact_grant_validation_keys()
        print("The response of CacheApi->get_artifact_grant_validation_keys:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->get_artifact_grant_validation_keys: %s\n" % e)
```



### Parameters

This endpoint does not need any parameter.

### Return type

[**ArtifactGrantValidationKeySet**](ArtifactGrantValidationKeySet.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: Not defined
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**401** | Unauthorized |  * WWW-Authenticate - Authentication challenge emitted by the configured ASP.NET Core authentication handler. <br>  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **refresh_cache_service**
> CacheRegistrationReturnValue refresh_cache_service(cache_registration_refresh_request)

Refresh a Grace Cache service registration.

Refreshes the current registration for the authenticated Cache service after the server-owned refresh-after interval. Refresh preserves the scopes, capabilities, and execution modes approved during registration.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.cache_registration_refresh_request import CacheRegistrationRefreshRequest
from grace_generated_openapi_probe.models.cache_registration_return_value import CacheRegistrationReturnValue
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
    api_instance = grace_generated_openapi_probe.CacheApi(api_client)
    cache_registration_refresh_request = grace_generated_openapi_probe.CacheRegistrationRefreshRequest() # CacheRegistrationRefreshRequest | 

    try:
        # Refresh a Grace Cache service registration.
        api_response = api_instance.refresh_cache_service(cache_registration_refresh_request)
        print("The response of CacheApi->refresh_cache_service:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->refresh_cache_service: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **cache_registration_refresh_request** | [**CacheRegistrationRefreshRequest**](CacheRegistrationRefreshRequest.md)|  | 

### Return type

[**CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**401** | Unauthorized |  * WWW-Authenticate - Authentication challenge emitted by the configured ASP.NET Core authentication handler. <br>  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **register_cache_service**
> CacheRegistrationReturnValue register_cache_service(cache_registration_request)

Register a Grace Cache service.

Registers or replaces the server-owned state for an approved Grace Cache service. The caller must authenticate with the configured OIDC JWT bearer service identity. Requested scopes and capabilities are persisted only when they are approved by server configuration.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.cache_registration_request import CacheRegistrationRequest
from grace_generated_openapi_probe.models.cache_registration_return_value import CacheRegistrationReturnValue
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
    api_instance = grace_generated_openapi_probe.CacheApi(api_client)
    cache_registration_request = grace_generated_openapi_probe.CacheRegistrationRequest() # CacheRegistrationRequest | 

    try:
        # Register a Grace Cache service.
        api_response = api_instance.register_cache_service(cache_registration_request)
        print("The response of CacheApi->register_cache_service:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->register_cache_service: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **cache_registration_request** | [**CacheRegistrationRequest**](CacheRegistrationRequest.md)|  | 

### Return type

[**CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**401** | Unauthorized |  * WWW-Authenticate - Authentication challenge emitted by the configured ASP.NET Core authentication handler. <br>  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

