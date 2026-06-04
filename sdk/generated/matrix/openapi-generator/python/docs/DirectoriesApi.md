# grace_generated_openapi_probe.DirectoriesApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_directory_version**](DirectoriesApi.md#create_directory_version) | **POST** /directory/create | Create a new directory version.
[**get_directory_version**](DirectoriesApi.md#get_directory_version) | **POST** /directory/get | Get a directory version.
[**get_directory_version_by_sha256_hash**](DirectoriesApi.md#get_directory_version_by_sha256_hash) | **POST** /directory/getBySha256Hash | Get a directory version by SHA-256 hash.
[**list_directory_versions_by_id**](DirectoriesApi.md#list_directory_versions_by_id) | **POST** /directory/getByDirectoryIds | List directory versions by id.
[**list_directory_versions_recursive**](DirectoriesApi.md#list_directory_versions_recursive) | **POST** /directory/getDirectoryVersionsRecursive | List a directory version and its children.
[**save_directory_versions**](DirectoriesApi.md#save_directory_versions) | **POST** /directory/saveDirectoryVersions | Save directory versions.


# **create_directory_version**
> DirectoryCommandReturnValue create_directory_version(create_parameters)

Create a new directory version.

Creates a directory version for the repository.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.create_parameters import CreateParameters
from grace_generated_openapi_probe.models.directory_command_return_value import DirectoryCommandReturnValue
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
    api_instance = grace_generated_openapi_probe.DirectoriesApi(api_client)
    create_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","DirectoryVersion":{"Class":"DirectoryVersion","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RelativePath":"src","Sha256Hash":"805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243","Directories":[],"Files":[],"Size":0,"CreatedAt":"2026-06-04T18:15:00Z","HashesValidated":true}} # CreateParameters | 

    try:
        # Create a new directory version.
        api_response = api_instance.create_directory_version(create_parameters)
        print("The response of DirectoriesApi->create_directory_version:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DirectoriesApi->create_directory_version: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_parameters** | [**CreateParameters**](CreateParameters.md)|  | 

### Return type

[**DirectoryCommandReturnValue**](DirectoryCommandReturnValue.md)

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

# **get_directory_version**
> DirectoryVersionReturnValue get_directory_version(get_parameters)

Get a directory version.

Gets a directory version DTO.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.directory_version_return_value import DirectoryVersionReturnValue
from grace_generated_openapi_probe.models.get_parameters import GetParameters
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
    api_instance = grace_generated_openapi_probe.DirectoriesApi(api_client)
    get_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842"} # GetParameters | 

    try:
        # Get a directory version.
        api_response = api_instance.get_directory_version(get_parameters)
        print("The response of DirectoriesApi->get_directory_version:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DirectoriesApi->get_directory_version: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_parameters** | [**GetParameters**](GetParameters.md)|  | 

### Return type

[**DirectoryVersionReturnValue**](DirectoryVersionReturnValue.md)

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

# **get_directory_version_by_sha256_hash**
> DirectoryVersionReturnValue get_directory_version_by_sha256_hash(get_by_sha256_hash_parameters)

Get a directory version by SHA-256 hash.

Gets a directory version DTO by SHA-256 hash.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.directory_version_return_value import DirectoryVersionReturnValue
from grace_generated_openapi_probe.models.get_by_sha256_hash_parameters import GetBySha256HashParameters
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
    api_instance = grace_generated_openapi_probe.DirectoriesApi(api_client)
    get_by_sha256_hash_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243"} # GetBySha256HashParameters | 

    try:
        # Get a directory version by SHA-256 hash.
        api_response = api_instance.get_directory_version_by_sha256_hash(get_by_sha256_hash_parameters)
        print("The response of DirectoriesApi->get_directory_version_by_sha256_hash:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DirectoriesApi->get_directory_version_by_sha256_hash: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_by_sha256_hash_parameters** | [**GetBySha256HashParameters**](GetBySha256HashParameters.md)|  | 

### Return type

[**DirectoryVersionReturnValue**](DirectoryVersionReturnValue.md)

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

# **list_directory_versions_by_id**
> DirectoryVersionListReturnValue list_directory_versions_by_id(get_by_directory_ids_parameters)

List directory versions by id.

Gets directory version DTOs by directory version ids.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.directory_version_list_return_value import DirectoryVersionListReturnValue
from grace_generated_openapi_probe.models.get_by_directory_ids_parameters import GetByDirectoryIdsParameters
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
    api_instance = grace_generated_openapi_probe.DirectoriesApi(api_client)
    get_by_directory_ids_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","DirectoryIds":["33a4e36b-828f-4fae-9343-50b6560dc842"]} # GetByDirectoryIdsParameters | 

    try:
        # List directory versions by id.
        api_response = api_instance.list_directory_versions_by_id(get_by_directory_ids_parameters)
        print("The response of DirectoriesApi->list_directory_versions_by_id:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DirectoriesApi->list_directory_versions_by_id: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_by_directory_ids_parameters** | [**GetByDirectoryIdsParameters**](GetByDirectoryIdsParameters.md)|  | 

### Return type

[**DirectoryVersionListReturnValue**](DirectoryVersionListReturnValue.md)

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

# **list_directory_versions_recursive**
> DirectoryVersionListReturnValue list_directory_versions_recursive(get_parameters)

List a directory version and its children.

Gets a directory version and all recursive child directory versions.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.directory_version_list_return_value import DirectoryVersionListReturnValue
from grace_generated_openapi_probe.models.get_parameters import GetParameters
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
    api_instance = grace_generated_openapi_probe.DirectoriesApi(api_client)
    get_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842"} # GetParameters | 

    try:
        # List a directory version and its children.
        api_response = api_instance.list_directory_versions_recursive(get_parameters)
        print("The response of DirectoriesApi->list_directory_versions_recursive:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DirectoriesApi->list_directory_versions_recursive: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_parameters** | [**GetParameters**](GetParameters.md)|  | 

### Return type

[**DirectoryVersionListReturnValue**](DirectoryVersionListReturnValue.md)

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

# **save_directory_versions**
> DirectoryCommandReturnValue save_directory_versions(save_directory_versions_parameters)

Save directory versions.

Saves a list of directory versions.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.directory_command_return_value import DirectoryCommandReturnValue
from grace_generated_openapi_probe.models.save_directory_versions_parameters import SaveDirectoryVersionsParameters
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
    api_instance = grace_generated_openapi_probe.DirectoriesApi(api_client)
    save_directory_versions_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","DirectoryVersions":[{"Class":"DirectoryVersion","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RelativePath":"src","Sha256Hash":"805331A98813206270E35564769E8BB59EEA02AEB7B27C7D6C63E625E1857243","Directories":[],"Files":[],"Size":0,"CreatedAt":"2026-06-04T18:15:00Z","HashesValidated":true}]} # SaveDirectoryVersionsParameters | 

    try:
        # Save directory versions.
        api_response = api_instance.save_directory_versions(save_directory_versions_parameters)
        print("The response of DirectoriesApi->save_directory_versions:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DirectoriesApi->save_directory_versions: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **save_directory_versions_parameters** | [**SaveDirectoryVersionsParameters**](SaveDirectoryVersionsParameters.md)|  | 

### Return type

[**DirectoryCommandReturnValue**](DirectoryCommandReturnValue.md)

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

