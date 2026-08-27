# grace_generated_openapi_probe.CacheApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**get_cache_artifact_grant_validation_key**](CacheApi.md#get_cache_artifact_grant_validation_key) | **GET** /cache/artifact-grant-validation-key | Get the current Server process public key for local Cache grant validation.
[**prepare_directory_version_zip**](CacheApi.md#prepare_directory_version_zip) | **POST** /cache/prepareDirectoryVersionZip | Prepare one Server-approved DirectoryVersion ZIP read and fill.
[**redeem_directory_version_zip_fill**](CacheApi.md#redeem_directory_version_zip_fill) | **POST** /cache/redeemDirectoryVersionZipFill | Redeem one permit and Cache process signature for a read-only ZIP source.


# **get_cache_artifact_grant_validation_key**
> CacheArtifactGrantValidationKey get_cache_artifact_grant_validation_key()

Get the current Server process public key for local Cache grant validation.

### Example


```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.cache_artifact_grant_validation_key import CacheArtifactGrantValidationKey
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)


# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.CacheApi(api_client)

    try:
        # Get the current Server process public key for local Cache grant validation.
        api_response = api_instance.get_cache_artifact_grant_validation_key()
        print("The response of CacheApi->get_cache_artifact_grant_validation_key:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->get_cache_artifact_grant_validation_key: %s\n" % e)
```



### Parameters

This endpoint does not need any parameter.

### Return type

[**CacheArtifactGrantValidationKey**](CacheArtifactGrantValidationKey.md)

### Authorization

No authorization required

### HTTP request headers

 - **Content-Type**: Not defined
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Current Server process public key and fixed Cache grant validation contract. |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **prepare_directory_version_zip**
> DirectoryVersionZipPreparation prepare_directory_version_zip(prepare_directory_version_zip_parameters)

Prepare one Server-approved DirectoryVersion ZIP read and fill.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.directory_version_zip_preparation import DirectoryVersionZipPreparation
from grace_generated_openapi_probe.models.prepare_directory_version_zip_parameters import PrepareDirectoryVersionZipParameters
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
    prepare_directory_version_zip_parameters = grace_generated_openapi_probe.PrepareDirectoryVersionZipParameters() # PrepareDirectoryVersionZipParameters | 

    try:
        # Prepare one Server-approved DirectoryVersion ZIP read and fill.
        api_response = api_instance.prepare_directory_version_zip(prepare_directory_version_zip_parameters)
        print("The response of CacheApi->prepare_directory_version_zip:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->prepare_directory_version_zip: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **prepare_directory_version_zip_parameters** | [**PrepareDirectoryVersionZipParameters**](PrepareDirectoryVersionZipParameters.md)|  | 

### Return type

[**DirectoryVersionZipPreparation**](DirectoryVersionZipPreparation.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Exact immutable artifact with a five-minute read grant and separate fill permit. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **redeem_directory_version_zip_fill**
> DirectoryVersionZipFillSource redeem_directory_version_zip_fill(redeem_directory_version_zip_fill_parameters)

Redeem one permit and Cache process signature for a read-only ZIP source.

### Example


```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.directory_version_zip_fill_source import DirectoryVersionZipFillSource
from grace_generated_openapi_probe.models.redeem_directory_version_zip_fill_parameters import RedeemDirectoryVersionZipFillParameters
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)


# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.CacheApi(api_client)
    redeem_directory_version_zip_fill_parameters = grace_generated_openapi_probe.RedeemDirectoryVersionZipFillParameters() # RedeemDirectoryVersionZipFillParameters | 

    try:
        # Redeem one permit and Cache process signature for a read-only ZIP source.
        api_response = api_instance.redeem_directory_version_zip_fill(redeem_directory_version_zip_fill_parameters)
        print("The response of CacheApi->redeem_directory_version_zip_fill:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling CacheApi->redeem_directory_version_zip_fill: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **redeem_directory_version_zip_fill_parameters** | [**RedeemDirectoryVersionZipFillParameters**](RedeemDirectoryVersionZipFillParameters.md)|  | 

### Return type

[**DirectoryVersionZipFillSource**](DirectoryVersionZipFillSource.md)

### Authorization

No authorization required

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Exact artifact and fresh read-only Blob source. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

