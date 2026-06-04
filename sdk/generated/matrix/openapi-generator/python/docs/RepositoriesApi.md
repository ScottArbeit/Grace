# grace_generated_openapi_probe.RepositoriesApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_repository**](RepositoriesApi.md#create_repository) | **POST** /repository/create | Create a new repository.
[**delete_repository**](RepositoriesApi.md#delete_repository) | **POST** /repository/delete | Delete a repository.
[**get_repository**](RepositoriesApi.md#get_repository) | **POST** /repository/get | Get a repository.
[**list_repository_branches**](RepositoriesApi.md#list_repository_branches) | **POST** /repository/getBranches | List repository branches.
[**list_repository_branches_by_branch_id**](RepositoriesApi.md#list_repository_branches_by_branch_id) | **POST** /repository/getBranchesByBranchId | List branches by branch id.
[**list_repository_references_by_reference_id**](RepositoriesApi.md#list_repository_references_by_reference_id) | **POST** /repository/getReferencesByReferenceId | List references by reference id.
[**repository_exists**](RepositoriesApi.md#repository_exists) | **POST** /repository/exists | Check whether a repository exists.
[**repository_is_empty**](RepositoriesApi.md#repository_is_empty) | **POST** /repository/isEmpty | Check whether a repository is empty.
[**set_repository_checkpoint_days**](RepositoriesApi.md#set_repository_checkpoint_days) | **POST** /repository/setCheckpointDays | Set checkpoint retention.
[**set_repository_default_server_api_version**](RepositoriesApi.md#set_repository_default_server_api_version) | **POST** /repository/setDefaultServerApiVersion | Set the default server API version.
[**set_repository_description**](RepositoriesApi.md#set_repository_description) | **POST** /repository/setDescription | Set repository description.
[**set_repository_name**](RepositoriesApi.md#set_repository_name) | **POST** /repository/setName | Set the name of a repository.
[**set_repository_record_saves**](RepositoriesApi.md#set_repository_record_saves) | **POST** /repository/setRecordSaves | Set whether saves are recorded.
[**set_repository_save_days**](RepositoriesApi.md#set_repository_save_days) | **POST** /repository/setSaveDays | Set save retention.
[**set_repository_status**](RepositoriesApi.md#set_repository_status) | **POST** /repository/setStatus | Set repository status.
[**set_repository_visibility**](RepositoriesApi.md#set_repository_visibility) | **POST** /repository/setVisibility | Set repository visibility.
[**undelete_repository**](RepositoriesApi.md#undelete_repository) | **POST** /repository/undelete | Undelete a previously deleted repository.


# **create_repository**
> RepositoryCommandReturnValue create_repository(create_repository_parameters)

Create a new repository.

Creates a repository in an organization.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.create_repository_parameters import CreateRepositoryParameters
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    create_repository_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage"} # CreateRepositoryParameters | 

    try:
        # Create a new repository.
        api_response = api_instance.create_repository(create_repository_parameters)
        print("The response of RepositoriesApi->create_repository:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->create_repository: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_repository_parameters** | [**CreateRepositoryParameters**](CreateRepositoryParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

# **delete_repository**
> RepositoryCommandReturnValue delete_repository(delete_repository_parameters)

Delete a repository.

Logically deletes a repository that exists and has not already been deleted.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.delete_repository_parameters import DeleteRepositoryParameters
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    delete_repository_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","Force":false,"DeleteReason":"Repository retired."} # DeleteRepositoryParameters | 

    try:
        # Delete a repository.
        api_response = api_instance.delete_repository(delete_repository_parameters)
        print("The response of RepositoriesApi->delete_repository:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->delete_repository: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **delete_repository_parameters** | [**DeleteRepositoryParameters**](DeleteRepositoryParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

# **get_repository**
> RepositoryReturnValue get_repository(repository_parameters)

Get a repository.

Gets repository metadata.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.repository_parameters import RepositoryParameters
from grace_generated_openapi_probe.models.repository_return_value import RepositoryReturnValue
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    repository_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage"} # RepositoryParameters | 

    try:
        # Get a repository.
        api_response = api_instance.get_repository(repository_parameters)
        print("The response of RepositoriesApi->get_repository:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->get_repository: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **repository_parameters** | [**RepositoryParameters**](RepositoryParameters.md)|  | 

### Return type

[**RepositoryReturnValue**](RepositoryReturnValue.md)

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

# **list_repository_branches**
> RepositoryBranchesReturnValue list_repository_branches(get_branches_parameters)

List repository branches.

Gets branches for the repository.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_branches_parameters import GetBranchesParameters
from grace_generated_openapi_probe.models.repository_branches_return_value import RepositoryBranchesReturnValue
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    get_branches_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","IncludeDeleted":false,"MaxCount":50} # GetBranchesParameters | 

    try:
        # List repository branches.
        api_response = api_instance.list_repository_branches(get_branches_parameters)
        print("The response of RepositoriesApi->list_repository_branches:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->list_repository_branches: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_branches_parameters** | [**GetBranchesParameters**](GetBranchesParameters.md)|  | 

### Return type

[**RepositoryBranchesReturnValue**](RepositoryBranchesReturnValue.md)

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

# **list_repository_branches_by_branch_id**
> RepositoryBranchesReturnValue list_repository_branches_by_branch_id(get_branches_by_branch_id_parameters)

List branches by branch id.

Gets branches in the repository by branch ids.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_branches_by_branch_id_parameters import GetBranchesByBranchIdParameters
from grace_generated_openapi_probe.models.repository_branches_return_value import RepositoryBranchesReturnValue
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    get_branches_by_branch_id_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","IncludeDeleted":false,"MaxCount":50,"BranchIds":["de7bf47d-23ae-4599-af68-68a317ea390d"]} # GetBranchesByBranchIdParameters | 

    try:
        # List branches by branch id.
        api_response = api_instance.list_repository_branches_by_branch_id(get_branches_by_branch_id_parameters)
        print("The response of RepositoriesApi->list_repository_branches_by_branch_id:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->list_repository_branches_by_branch_id: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_branches_by_branch_id_parameters** | [**GetBranchesByBranchIdParameters**](GetBranchesByBranchIdParameters.md)|  | 

### Return type

[**RepositoryBranchesReturnValue**](RepositoryBranchesReturnValue.md)

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

# **list_repository_references_by_reference_id**
> RepositoryReferencesReturnValue list_repository_references_by_reference_id(get_references_by_reference_id_parameters)

List references by reference id.

Gets references in the repository by reference ids.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_references_by_reference_id_parameters import GetReferencesByReferenceIdParameters
from grace_generated_openapi_probe.models.repository_references_return_value import RepositoryReferencesReturnValue
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    get_references_by_reference_id_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","ReferenceIds":["c8f9bac8-d489-46c7-917f-b36b7d9efa9a"],"MaxCount":50} # GetReferencesByReferenceIdParameters | 

    try:
        # List references by reference id.
        api_response = api_instance.list_repository_references_by_reference_id(get_references_by_reference_id_parameters)
        print("The response of RepositoriesApi->list_repository_references_by_reference_id:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->list_repository_references_by_reference_id: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_references_by_reference_id_parameters** | [**GetReferencesByReferenceIdParameters**](GetReferencesByReferenceIdParameters.md)|  | 

### Return type

[**RepositoryReferencesReturnValue**](RepositoryReferencesReturnValue.md)

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

# **repository_exists**
> RepositoryBooleanReturnValue repository_exists(repository_parameters)

Check whether a repository exists.

Checks whether a repository exists for the supplied selector values.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.repository_boolean_return_value import RepositoryBooleanReturnValue
from grace_generated_openapi_probe.models.repository_parameters import RepositoryParameters
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    repository_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage"} # RepositoryParameters | 

    try:
        # Check whether a repository exists.
        api_response = api_instance.repository_exists(repository_parameters)
        print("The response of RepositoriesApi->repository_exists:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->repository_exists: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **repository_parameters** | [**RepositoryParameters**](RepositoryParameters.md)|  | 

### Return type

[**RepositoryBooleanReturnValue**](RepositoryBooleanReturnValue.md)

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

# **repository_is_empty**
> RepositoryBooleanReturnValue repository_is_empty(is_empty_parameters)

Check whether a repository is empty.

Checks whether a repository has no content yet.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.is_empty_parameters import IsEmptyParameters
from grace_generated_openapi_probe.models.repository_boolean_return_value import RepositoryBooleanReturnValue
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    is_empty_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage"} # IsEmptyParameters | 

    try:
        # Check whether a repository is empty.
        api_response = api_instance.repository_is_empty(is_empty_parameters)
        print("The response of RepositoriesApi->repository_is_empty:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->repository_is_empty: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **is_empty_parameters** | [**IsEmptyParameters**](IsEmptyParameters.md)|  | 

### Return type

[**RepositoryBooleanReturnValue**](RepositoryBooleanReturnValue.md)

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

# **set_repository_checkpoint_days**
> RepositoryCommandReturnValue set_repository_checkpoint_days(set_checkpoint_days_parameters)

Set checkpoint retention.

Sets the number of days to keep checkpoints in the repository.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
from grace_generated_openapi_probe.models.set_checkpoint_days_parameters import SetCheckpointDaysParameters
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    set_checkpoint_days_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","CheckpointDays":90} # SetCheckpointDaysParameters | 

    try:
        # Set checkpoint retention.
        api_response = api_instance.set_repository_checkpoint_days(set_checkpoint_days_parameters)
        print("The response of RepositoriesApi->set_repository_checkpoint_days:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->set_repository_checkpoint_days: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_checkpoint_days_parameters** | [**SetCheckpointDaysParameters**](SetCheckpointDaysParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

# **set_repository_default_server_api_version**
> RepositoryCommandReturnValue set_repository_default_server_api_version(set_default_server_api_version_parameters)

Set the default server API version.

Sets the default server API version clients should use for the repository.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
from grace_generated_openapi_probe.models.set_default_server_api_version_parameters import SetDefaultServerApiVersionParameters
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    set_default_server_api_version_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","DefaultServerApiVersion":"2026-06-04"} # SetDefaultServerApiVersionParameters | 

    try:
        # Set the default server API version.
        api_response = api_instance.set_repository_default_server_api_version(set_default_server_api_version_parameters)
        print("The response of RepositoriesApi->set_repository_default_server_api_version:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->set_repository_default_server_api_version: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_default_server_api_version_parameters** | [**SetDefaultServerApiVersionParameters**](SetDefaultServerApiVersionParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

# **set_repository_description**
> RepositoryCommandReturnValue set_repository_description(set_repository_description_parameters)

Set repository description.

Sets the repository description.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
from grace_generated_openapi_probe.models.set_repository_description_parameters import SetRepositoryDescriptionParameters
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    set_repository_description_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","Description":"Repository for release candidates."} # SetRepositoryDescriptionParameters | 

    try:
        # Set repository description.
        api_response = api_instance.set_repository_description(set_repository_description_parameters)
        print("The response of RepositoriesApi->set_repository_description:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->set_repository_description: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_repository_description_parameters** | [**SetRepositoryDescriptionParameters**](SetRepositoryDescriptionParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

# **set_repository_name**
> RepositoryCommandReturnValue set_repository_name(set_repository_name_parameters)

Set the name of a repository.

Renames a repository that exists and has not been deleted.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
from grace_generated_openapi_probe.models.set_repository_name_parameters import SetRepositoryNameParameters
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    set_repository_name_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","NewName":"sample-repository-renamed"} # SetRepositoryNameParameters | 

    try:
        # Set the name of a repository.
        api_response = api_instance.set_repository_name(set_repository_name_parameters)
        print("The response of RepositoriesApi->set_repository_name:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->set_repository_name: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_repository_name_parameters** | [**SetRepositoryNameParameters**](SetRepositoryNameParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

# **set_repository_record_saves**
> RepositoryCommandReturnValue set_repository_record_saves(record_saves_parameters)

Set whether saves are recorded.

Sets the repository default for whether save references should be kept.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.record_saves_parameters import RecordSavesParameters
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    record_saves_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","RecordSaves":true} # RecordSavesParameters | 

    try:
        # Set whether saves are recorded.
        api_response = api_instance.set_repository_record_saves(record_saves_parameters)
        print("The response of RepositoriesApi->set_repository_record_saves:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->set_repository_record_saves: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **record_saves_parameters** | [**RecordSavesParameters**](RecordSavesParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

# **set_repository_save_days**
> RepositoryCommandReturnValue set_repository_save_days(set_save_days_parameters)

Set save retention.

Sets the number of days to keep saves in the repository.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
from grace_generated_openapi_probe.models.set_save_days_parameters import SetSaveDaysParameters
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    set_save_days_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","SaveDays":30} # SetSaveDaysParameters | 

    try:
        # Set save retention.
        api_response = api_instance.set_repository_save_days(set_save_days_parameters)
        print("The response of RepositoriesApi->set_repository_save_days:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->set_repository_save_days: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_save_days_parameters** | [**SetSaveDaysParameters**](SetSaveDaysParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

# **set_repository_status**
> RepositoryCommandReturnValue set_repository_status(set_repository_status_parameters)

Set repository status.

Sets the repository operational status.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
from grace_generated_openapi_probe.models.set_repository_status_parameters import SetRepositoryStatusParameters
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    set_repository_status_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","Status":"Active"} # SetRepositoryStatusParameters | 

    try:
        # Set repository status.
        api_response = api_instance.set_repository_status(set_repository_status_parameters)
        print("The response of RepositoriesApi->set_repository_status:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->set_repository_status: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_repository_status_parameters** | [**SetRepositoryStatusParameters**](SetRepositoryStatusParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

# **set_repository_visibility**
> RepositoryCommandReturnValue set_repository_visibility(set_repository_visibility_parameters)

Set repository visibility.

Sets whether the repository is public or private.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
from grace_generated_openapi_probe.models.set_repository_visibility_parameters import SetRepositoryVisibilityParameters
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    set_repository_visibility_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage","Visibility":"Private"} # SetRepositoryVisibilityParameters | 

    try:
        # Set repository visibility.
        api_response = api_instance.set_repository_visibility(set_repository_visibility_parameters)
        print("The response of RepositoriesApi->set_repository_visibility:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->set_repository_visibility: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_repository_visibility_parameters** | [**SetRepositoryVisibilityParameters**](SetRepositoryVisibilityParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

# **undelete_repository**
> RepositoryCommandReturnValue undelete_repository(undelete_repository_parameters)

Undelete a previously deleted repository.

Restores a logically deleted repository.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue
from grace_generated_openapi_probe.models.undelete_repository_parameters import UndeleteRepositoryParameters
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
    api_instance = grace_generated_openapi_probe.RepositoriesApi(api_client)
    undelete_repository_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","RepositoryName":"sample-repository","ObjectStorageProvider":"AzureBlobStorage"} # UndeleteRepositoryParameters | 

    try:
        # Undelete a previously deleted repository.
        api_response = api_instance.undelete_repository(undelete_repository_parameters)
        print("The response of RepositoriesApi->undelete_repository:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling RepositoriesApi->undelete_repository: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **undelete_repository_parameters** | [**UndeleteRepositoryParameters**](UndeleteRepositoryParameters.md)|  | 

### Return type

[**RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

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

