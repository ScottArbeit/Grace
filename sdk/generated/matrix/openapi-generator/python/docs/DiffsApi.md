# grace_generated_openapi_probe.DiffsApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**get_diff**](DiffsApi.md#get_diff) | **POST** /diff/getDiff | Get a diff.
[**get_diff_by_sha256_hash**](DiffsApi.md#get_diff_by_sha256_hash) | **POST** /diff/getDiffBySha256Hash | Get a diff by SHA-256 hash.
[**populate_diff**](DiffsApi.md#populate_diff) | **POST** /diff/populate | Populate a diff actor.


# **get_diff**
> DiffReturnValue get_diff(get_diff_parameters)

Get a diff.

Retrieves the contents of the diff between two directory versions.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.diff_return_value import DiffReturnValue
from grace_generated_openapi_probe.models.get_diff_parameters import GetDiffParameters
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
    api_instance = grace_generated_openapi_probe.DiffsApi(api_client)
    get_diff_parameters = {CorrelationId=cli-20260604T181500Z-0001, Principal=user:alice, OwnerId=9dd5f81f-dc43-4839-9173-85d09394f30f, OrganizationId=e35d64a9-b990-44f5-bf02-32ad7d15630c, RepositoryId=ab6f35ef-6e01-440b-8f9b-c343a5272095, DirectoryVersionId1=33a4e36b-828f-4fae-9343-50b6560dc842, DirectoryVersionId2=66b7b8c2-8d2f-4f04-951c-6b3486c4e5d1} # GetDiffParameters | 

    try:
        # Get a diff.
        api_response = api_instance.get_diff(get_diff_parameters)
        print("The response of DiffsApi->get_diff:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DiffsApi->get_diff: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_diff_parameters** | [**GetDiffParameters**](GetDiffParameters.md)|  | 

### Return type

[**DiffReturnValue**](DiffReturnValue.md)

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

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_diff_by_sha256_hash**
> DiffReturnValue get_diff_by_sha256_hash(get_diff_by_sha256_hash_parameters)

Get a diff by SHA-256 hash.

Retrieves a diff by comparing two directory versions identified by SHA-256 hash.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.diff_return_value import DiffReturnValue
from grace_generated_openapi_probe.models.get_diff_by_sha256_hash_parameters import GetDiffBySha256HashParameters
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
    api_instance = grace_generated_openapi_probe.DiffsApi(api_client)
    get_diff_by_sha256_hash_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId1":"33a4e36b-828f-4fae-9343-50b6560dc842","DirectoryVersionId2":"66b7b8c2-8d2f-4f04-951c-6b3486c4e5d1","Sha256Hash1":"805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243","Sha256Hash2":"A05331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857240"} # GetDiffBySha256HashParameters | 

    try:
        # Get a diff by SHA-256 hash.
        api_response = api_instance.get_diff_by_sha256_hash(get_diff_by_sha256_hash_parameters)
        print("The response of DiffsApi->get_diff_by_sha256_hash:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DiffsApi->get_diff_by_sha256_hash: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_diff_by_sha256_hash_parameters** | [**GetDiffBySha256HashParameters**](GetDiffBySha256HashParameters.md)|  | 

### Return type

[**DiffReturnValue**](DiffReturnValue.md)

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

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **populate_diff**
> DiffPopulateReturnValue populate_diff(populate_parameters)

Populate a diff actor.

Populates the diff actor without returning the diff. This endpoint is meant to be used when generating the diff
through reacting to an event.


### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.diff_populate_return_value import DiffPopulateReturnValue
from grace_generated_openapi_probe.models.populate_parameters import PopulateParameters
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
    api_instance = grace_generated_openapi_probe.DiffsApi(api_client)
    populate_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId1":"33a4e36b-828f-4fae-9343-50b6560dc842","DirectoryVersionId2":"66b7b8c2-8d2f-4f04-951c-6b3486c4e5d1"} # PopulateParameters | 

    try:
        # Populate a diff actor.
        api_response = api_instance.populate_diff(populate_parameters)
        print("The response of DiffsApi->populate_diff:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DiffsApi->populate_diff: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **populate_parameters** | [**PopulateParameters**](PopulateParameters.md)|  | 

### Return type

[**DiffPopulateReturnValue**](DiffPopulateReturnValue.md)

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

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

