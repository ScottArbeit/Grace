# grace_generated_openapi_probe.OrganizationsApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_organization**](OrganizationsApi.md#create_organization) | **POST** /organization/create | Create an organization.
[**delete_organization**](OrganizationsApi.md#delete_organization) | **POST** /organization/delete | Delete an organization.
[**get_organization**](OrganizationsApi.md#get_organization) | **POST** /organization/get | Get an organization.
[**list_organization_repositories**](OrganizationsApi.md#list_organization_repositories) | **POST** /organization/listRepositories | List repositories of an organization.
[**set_organization_description**](OrganizationsApi.md#set_organization_description) | **POST** /organization/setDescription | Set the organization&#39;s description.
[**set_organization_name**](OrganizationsApi.md#set_organization_name) | **POST** /organization/setName | Set the name of an organization.
[**set_organization_search_visibility**](OrganizationsApi.md#set_organization_search_visibility) | **POST** /organization/setSearchVisibility | Set organization search visibility.
[**set_organization_type**](OrganizationsApi.md#set_organization_type) | **POST** /organization/setType | Set the organization type.
[**undelete_organization**](OrganizationsApi.md#undelete_organization) | **POST** /organization/undelete | Undelete a previously deleted organization.


# **create_organization**
> OrganizationCommandReturnValue create_organization(create_organization_parameters)

Create an organization.

Creates an organization under an owner.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.create_organization_parameters import CreateOrganizationParameters
from grace_generated_openapi_probe.models.organization_command_return_value import OrganizationCommandReturnValue
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
    api_instance = grace_generated_openapi_probe.OrganizationsApi(api_client)
    create_organization_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org"} # CreateOrganizationParameters | 

    try:
        # Create an organization.
        api_response = api_instance.create_organization(create_organization_parameters)
        print("The response of OrganizationsApi->create_organization:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OrganizationsApi->create_organization: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_organization_parameters** | [**CreateOrganizationParameters**](CreateOrganizationParameters.md)|  | 

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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

# **delete_organization**
> OrganizationCommandReturnValue delete_organization(delete_organization_parameters)

Delete an organization.

Logically deletes an organization that exists and has not already been deleted.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.delete_organization_parameters import DeleteOrganizationParameters
from grace_generated_openapi_probe.models.organization_command_return_value import OrganizationCommandReturnValue
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
    api_instance = grace_generated_openapi_probe.OrganizationsApi(api_client)
    delete_organization_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","Force":false,"DeleteReason":"Organization retired."} # DeleteOrganizationParameters | 

    try:
        # Delete an organization.
        api_response = api_instance.delete_organization(delete_organization_parameters)
        print("The response of OrganizationsApi->delete_organization:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OrganizationsApi->delete_organization: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **delete_organization_parameters** | [**DeleteOrganizationParameters**](DeleteOrganizationParameters.md)|  | 

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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

# **get_organization**
> OrganizationReturnValue get_organization(get_organization_parameters)

Get an organization.

Gets organization metadata.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_organization_parameters import GetOrganizationParameters
from grace_generated_openapi_probe.models.organization_return_value import OrganizationReturnValue
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
    api_instance = grace_generated_openapi_probe.OrganizationsApi(api_client)
    get_organization_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","IncludeDeleted":false} # GetOrganizationParameters | 

    try:
        # Get an organization.
        api_response = api_instance.get_organization(get_organization_parameters)
        print("The response of OrganizationsApi->get_organization:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OrganizationsApi->get_organization: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_organization_parameters** | [**GetOrganizationParameters**](GetOrganizationParameters.md)|  | 

### Return type

[**OrganizationReturnValue**](OrganizationReturnValue.md)

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

# **list_organization_repositories**
> Dict[str, str] list_organization_repositories(list_repositories_parameters)

List repositories of an organization.

Lists repositories associated with the organization.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.list_repositories_parameters import ListRepositoriesParameters
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
    api_instance = grace_generated_openapi_probe.OrganizationsApi(api_client)
    list_repositories_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org"} # ListRepositoriesParameters | 

    try:
        # List repositories of an organization.
        api_response = api_instance.list_organization_repositories(list_repositories_parameters)
        print("The response of OrganizationsApi->list_organization_repositories:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OrganizationsApi->list_organization_repositories: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **list_repositories_parameters** | [**ListRepositoriesParameters**](ListRepositoriesParameters.md)|  | 

### Return type

**Dict[str, str]**

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

# **set_organization_description**
> OrganizationCommandReturnValue set_organization_description(set_organization_description_parameters)

Set the organization's description.

Sets the organization description.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.organization_command_return_value import OrganizationCommandReturnValue
from grace_generated_openapi_probe.models.set_organization_description_parameters import SetOrganizationDescriptionParameters
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
    api_instance = grace_generated_openapi_probe.OrganizationsApi(api_client)
    set_organization_description_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","Description":"Engineering organization for Alice's projects."} # SetOrganizationDescriptionParameters | 

    try:
        # Set the organization's description.
        api_response = api_instance.set_organization_description(set_organization_description_parameters)
        print("The response of OrganizationsApi->set_organization_description:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OrganizationsApi->set_organization_description: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_organization_description_parameters** | [**SetOrganizationDescriptionParameters**](SetOrganizationDescriptionParameters.md)|  | 

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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

# **set_organization_name**
> OrganizationCommandReturnValue set_organization_name(set_organization_name_parameters)

Set the name of an organization.

Renames an organization that exists and has not been deleted.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.organization_command_return_value import OrganizationCommandReturnValue
from grace_generated_openapi_probe.models.set_organization_name_parameters import SetOrganizationNameParameters
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
    api_instance = grace_generated_openapi_probe.OrganizationsApi(api_client)
    set_organization_name_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","NewName":"alice-platform"} # SetOrganizationNameParameters | 

    try:
        # Set the name of an organization.
        api_response = api_instance.set_organization_name(set_organization_name_parameters)
        print("The response of OrganizationsApi->set_organization_name:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OrganizationsApi->set_organization_name: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_organization_name_parameters** | [**SetOrganizationNameParameters**](SetOrganizationNameParameters.md)|  | 

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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

# **set_organization_search_visibility**
> OrganizationCommandReturnValue set_organization_search_visibility(set_organization_search_visibility_parameters)

Set organization search visibility.

Sets whether the organization appears in search results.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.organization_command_return_value import OrganizationCommandReturnValue
from grace_generated_openapi_probe.models.set_organization_search_visibility_parameters import SetOrganizationSearchVisibilityParameters
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
    api_instance = grace_generated_openapi_probe.OrganizationsApi(api_client)
    set_organization_search_visibility_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","SearchVisibility":"Visible"} # SetOrganizationSearchVisibilityParameters | 

    try:
        # Set organization search visibility.
        api_response = api_instance.set_organization_search_visibility(set_organization_search_visibility_parameters)
        print("The response of OrganizationsApi->set_organization_search_visibility:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OrganizationsApi->set_organization_search_visibility: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_organization_search_visibility_parameters** | [**SetOrganizationSearchVisibilityParameters**](SetOrganizationSearchVisibilityParameters.md)|  | 

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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

# **set_organization_type**
> OrganizationCommandReturnValue set_organization_type(set_organization_type_parameters)

Set the organization type.

Sets the organization type to a public or private value accepted by the server.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.organization_command_return_value import OrganizationCommandReturnValue
from grace_generated_openapi_probe.models.set_organization_type_parameters import SetOrganizationTypeParameters
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
    api_instance = grace_generated_openapi_probe.OrganizationsApi(api_client)
    set_organization_type_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org","OrganizationType":"Public"} # SetOrganizationTypeParameters | 

    try:
        # Set the organization type.
        api_response = api_instance.set_organization_type(set_organization_type_parameters)
        print("The response of OrganizationsApi->set_organization_type:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OrganizationsApi->set_organization_type: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_organization_type_parameters** | [**SetOrganizationTypeParameters**](SetOrganizationTypeParameters.md)|  | 

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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

# **undelete_organization**
> OrganizationCommandReturnValue undelete_organization(undelete_organization_parameters)

Undelete a previously deleted organization.

Restores a logically deleted organization.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.organization_command_return_value import OrganizationCommandReturnValue
from grace_generated_openapi_probe.models.undelete_organization_parameters import UndeleteOrganizationParameters
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
    api_instance = grace_generated_openapi_probe.OrganizationsApi(api_client)
    undelete_organization_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","OrganizationName":"alice-org"} # UndeleteOrganizationParameters | 

    try:
        # Undelete a previously deleted organization.
        api_response = api_instance.undelete_organization(undelete_organization_parameters)
        print("The response of OrganizationsApi->undelete_organization:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OrganizationsApi->undelete_organization: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **undelete_organization_parameters** | [**UndeleteOrganizationParameters**](UndeleteOrganizationParameters.md)|  | 

### Return type

[**OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

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

