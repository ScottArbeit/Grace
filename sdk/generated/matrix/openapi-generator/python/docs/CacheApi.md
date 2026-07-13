# grace_generated_openapi_probe.CacheApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**assign_cache_repositories**](CacheApi.md#assign_cache_repositories) | **POST** /cache/assign-repositories | Replace a Cache&#39;s exact repository assignments as a current administrator.
[**enroll_cache**](CacheApi.md#enroll_cache) | **POST** /cache/enroll | Enroll a Grace Cache with an administrator-authorized repository boundary.
[**get_artifact_grant_validation_keys**](CacheApi.md#get_artifact_grant_validation_keys) | **GET** /cache/validation-keys | Publish artifact grant validation keys.
[**refresh_cache**](CacheApi.md#refresh_cache) | **POST** /cache/refresh | Refresh Cache operational facts with a current identity-key proof.
[**revoke_cache**](CacheApi.md#revoke_cache) | **POST** /cache/revoke | Revoke a Cache registration as a current administrator.
[**rotate_cache_key**](CacheApi.md#rotate_cache_key) | **POST** /cache/rotate-key | Rotate a Cache identity key after proof by the currently accepted key.


# **assign_cache_repositories**
> CacheRegistrationReturnValue assign_cache_repositories(cache_repository_assignment_request)

Replace a Cache's exact repository assignments as a current administrator.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.cache_registration_return_value import CacheRegistrationReturnValue
from grace_generated_openapi_probe.models.cache_repository_assignment_request import CacheRepositoryAssignmentRequest
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
    cache_repository_assignment_request = grace_generated_openapi_probe.CacheRepositoryAssignmentRequest() # CacheRepositoryAssignmentRequest | 

    try:
        # Replace a Cache's exact repository assignments as a current administrator.
        api_response = api_instance.assign_cache_repositories(cache_repository_assignment_request)
        print("The response of CacheApi->assign_cache_repositories:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->assign_cache_repositories: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **cache_repository_assignment_request** | [**CacheRepositoryAssignmentRequest**](CacheRepositoryAssignmentRequest.md)|  | 

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
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **enroll_cache**
> CacheRegistrationReturnValue enroll_cache(cache_enrollment_request)

Enroll a Grace Cache with an administrator-authorized repository boundary.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.cache_enrollment_request import CacheEnrollmentRequest
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
    cache_enrollment_request = grace_generated_openapi_probe.CacheEnrollmentRequest() # CacheEnrollmentRequest | 

    try:
        # Enroll a Grace Cache with an administrator-authorized repository boundary.
        api_response = api_instance.enroll_cache(cache_enrollment_request)
        print("The response of CacheApi->enroll_cache:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->enroll_cache: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **cache_enrollment_request** | [**CacheEnrollmentRequest**](CacheEnrollmentRequest.md)|  | 

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
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_artifact_grant_validation_keys**
> ArtifactGrantValidationKeySet get_artifact_grant_validation_keys()

Publish artifact grant validation keys.

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

# **refresh_cache**
> CacheRegistrationReturnValue refresh_cache(cache_registration_refresh_request)

Refresh Cache operational facts with a current identity-key proof.

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
        # Refresh Cache operational facts with a current identity-key proof.
        api_response = api_instance.refresh_cache(cache_registration_refresh_request)
        print("The response of CacheApi->refresh_cache:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->refresh_cache: %s\n" % e)
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

# **revoke_cache**
> CacheRegistrationReturnValue revoke_cache(cache_revocation_request)

Revoke a Cache registration as a current administrator.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.cache_registration_return_value import CacheRegistrationReturnValue
from grace_generated_openapi_probe.models.cache_revocation_request import CacheRevocationRequest
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
    cache_revocation_request = grace_generated_openapi_probe.CacheRevocationRequest() # CacheRevocationRequest | 

    try:
        # Revoke a Cache registration as a current administrator.
        api_response = api_instance.revoke_cache(cache_revocation_request)
        print("The response of CacheApi->revoke_cache:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->revoke_cache: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **cache_revocation_request** | [**CacheRevocationRequest**](CacheRevocationRequest.md)|  | 

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
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **rotate_cache_key**
> CacheRegistrationReturnValue rotate_cache_key(cache_key_rotation_request)

Rotate a Cache identity key after proof by the currently accepted key.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.cache_key_rotation_request import CacheKeyRotationRequest
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
    cache_key_rotation_request = grace_generated_openapi_probe.CacheKeyRotationRequest() # CacheKeyRotationRequest | 

    try:
        # Rotate a Cache identity key after proof by the currently accepted key.
        api_response = api_instance.rotate_cache_key(cache_key_rotation_request)
        print("The response of CacheApi->rotate_cache_key:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->rotate_cache_key: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **cache_key_rotation_request** | [**CacheKeyRotationRequest**](CacheKeyRotationRequest.md)|  | 

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

