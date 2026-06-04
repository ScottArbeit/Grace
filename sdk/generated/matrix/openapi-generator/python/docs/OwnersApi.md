# grace_generated_openapi_probe.OwnersApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_owner**](OwnersApi.md#create_owner) | **POST** /owner/create | Create an owner.
[**delete_owner**](OwnersApi.md#delete_owner) | **POST** /owner/delete | Delete an owner.
[**get_owner**](OwnersApi.md#get_owner) | **POST** /owner/get | Get an owner.
[**list_owner_organizations**](OwnersApi.md#list_owner_organizations) | **POST** /owner/listOrganizations | List the organizations for an owner.
[**set_owner_description**](OwnersApi.md#set_owner_description) | **POST** /owner/setDescription | Set the owner&#39;s description.
[**set_owner_name**](OwnersApi.md#set_owner_name) | **POST** /owner/setName | Set the name of an owner.
[**set_owner_search_visibility**](OwnersApi.md#set_owner_search_visibility) | **POST** /owner/setSearchVisibility | Set owner search visibility.
[**set_owner_type**](OwnersApi.md#set_owner_type) | **POST** /owner/setType | Set the owner type.
[**undelete_owner**](OwnersApi.md#undelete_owner) | **POST** /owner/undelete | Undelete a previously deleted owner.


# **create_owner**
> OwnerCommandReturnValue create_owner(create_owner_parameters)

Create an owner.

Creates an owner with the specified id and name.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.create_owner_parameters import CreateOwnerParameters
from grace_generated_openapi_probe.models.owner_command_return_value import OwnerCommandReturnValue
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
    api_instance = grace_generated_openapi_probe.OwnersApi(api_client)
    create_owner_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice"} # CreateOwnerParameters | 

    try:
        # Create an owner.
        api_response = api_instance.create_owner(create_owner_parameters)
        print("The response of OwnersApi->create_owner:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OwnersApi->create_owner: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_owner_parameters** | [**CreateOwnerParameters**](CreateOwnerParameters.md)|  | 

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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

# **delete_owner**
> OwnerCommandReturnValue delete_owner(delete_owner_parameters)

Delete an owner.

Logically deletes an owner that exists and has not already been deleted.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.delete_owner_parameters import DeleteOwnerParameters
from grace_generated_openapi_probe.models.owner_command_return_value import OwnerCommandReturnValue
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
    api_instance = grace_generated_openapi_probe.OwnersApi(api_client)
    delete_owner_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","Force":false,"DeleteReason":"Owner retired."} # DeleteOwnerParameters | 

    try:
        # Delete an owner.
        api_response = api_instance.delete_owner(delete_owner_parameters)
        print("The response of OwnersApi->delete_owner:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OwnersApi->delete_owner: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **delete_owner_parameters** | [**DeleteOwnerParameters**](DeleteOwnerParameters.md)|  | 

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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

# **get_owner**
> OwnerReturnValue get_owner(get_owner_parameters)

Get an owner.

Gets owner metadata.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_owner_parameters import GetOwnerParameters
from grace_generated_openapi_probe.models.owner_return_value import OwnerReturnValue
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
    api_instance = grace_generated_openapi_probe.OwnersApi(api_client)
    get_owner_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","IncludeDeleted":false} # GetOwnerParameters | 

    try:
        # Get an owner.
        api_response = api_instance.get_owner(get_owner_parameters)
        print("The response of OwnersApi->get_owner:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OwnersApi->get_owner: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_owner_parameters** | [**GetOwnerParameters**](GetOwnerParameters.md)|  | 

### Return type

[**OwnerReturnValue**](OwnerReturnValue.md)

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

# **list_owner_organizations**
> Dict[str, str] list_owner_organizations(list_organizations_parameters)

List the organizations for an owner.

Lists organizations associated with the owner.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.list_organizations_parameters import ListOrganizationsParameters
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
    api_instance = grace_generated_openapi_probe.OwnersApi(api_client)
    list_organizations_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice"} # ListOrganizationsParameters | 

    try:
        # List the organizations for an owner.
        api_response = api_instance.list_owner_organizations(list_organizations_parameters)
        print("The response of OwnersApi->list_owner_organizations:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OwnersApi->list_owner_organizations: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **list_organizations_parameters** | [**ListOrganizationsParameters**](ListOrganizationsParameters.md)|  | 

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

# **set_owner_description**
> OwnerCommandReturnValue set_owner_description(set_owner_description_parameters)

Set the owner's description.

Sets the owner description.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.owner_command_return_value import OwnerCommandReturnValue
from grace_generated_openapi_probe.models.set_owner_description_parameters import SetOwnerDescriptionParameters
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
    api_instance = grace_generated_openapi_probe.OwnersApi(api_client)
    set_owner_description_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","Description":"Source control owner for Alice's projects."} # SetOwnerDescriptionParameters | 

    try:
        # Set the owner's description.
        api_response = api_instance.set_owner_description(set_owner_description_parameters)
        print("The response of OwnersApi->set_owner_description:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OwnersApi->set_owner_description: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_owner_description_parameters** | [**SetOwnerDescriptionParameters**](SetOwnerDescriptionParameters.md)|  | 

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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

# **set_owner_name**
> OwnerCommandReturnValue set_owner_name(set_owner_name_parameters)

Set the name of an owner.

Renames an owner that exists and has not been deleted.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.owner_command_return_value import OwnerCommandReturnValue
from grace_generated_openapi_probe.models.set_owner_name_parameters import SetOwnerNameParameters
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
    api_instance = grace_generated_openapi_probe.OwnersApi(api_client)
    set_owner_name_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","NewName":"alice-renamed"} # SetOwnerNameParameters | 

    try:
        # Set the name of an owner.
        api_response = api_instance.set_owner_name(set_owner_name_parameters)
        print("The response of OwnersApi->set_owner_name:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OwnersApi->set_owner_name: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_owner_name_parameters** | [**SetOwnerNameParameters**](SetOwnerNameParameters.md)|  | 

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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

# **set_owner_search_visibility**
> OwnerCommandReturnValue set_owner_search_visibility(set_owner_search_visibility_parameters)

Set owner search visibility.

Sets whether the owner appears in search results.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.owner_command_return_value import OwnerCommandReturnValue
from grace_generated_openapi_probe.models.set_owner_search_visibility_parameters import SetOwnerSearchVisibilityParameters
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
    api_instance = grace_generated_openapi_probe.OwnersApi(api_client)
    set_owner_search_visibility_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","SearchVisibility":"Visible"} # SetOwnerSearchVisibilityParameters | 

    try:
        # Set owner search visibility.
        api_response = api_instance.set_owner_search_visibility(set_owner_search_visibility_parameters)
        print("The response of OwnersApi->set_owner_search_visibility:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OwnersApi->set_owner_search_visibility: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_owner_search_visibility_parameters** | [**SetOwnerSearchVisibilityParameters**](SetOwnerSearchVisibilityParameters.md)|  | 

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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

# **set_owner_type**
> OwnerCommandReturnValue set_owner_type(set_owner_type_parameters)

Set the owner type.

Sets the owner type to a public or private value accepted by the server.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.owner_command_return_value import OwnerCommandReturnValue
from grace_generated_openapi_probe.models.set_owner_type_parameters import SetOwnerTypeParameters
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
    api_instance = grace_generated_openapi_probe.OwnersApi(api_client)
    set_owner_type_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice","OwnerType":"Public"} # SetOwnerTypeParameters | 

    try:
        # Set the owner type.
        api_response = api_instance.set_owner_type(set_owner_type_parameters)
        print("The response of OwnersApi->set_owner_type:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OwnersApi->set_owner_type: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_owner_type_parameters** | [**SetOwnerTypeParameters**](SetOwnerTypeParameters.md)|  | 

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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

# **undelete_owner**
> OwnerCommandReturnValue undelete_owner(undelete_owner_parameters)

Undelete a previously deleted owner.

Restores a logically deleted owner.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.owner_command_return_value import OwnerCommandReturnValue
from grace_generated_openapi_probe.models.undelete_owner_parameters import UndeleteOwnerParameters
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
    api_instance = grace_generated_openapi_probe.OwnersApi(api_client)
    undelete_owner_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OwnerName":"alice"} # UndeleteOwnerParameters | 

    try:
        # Undelete a previously deleted owner.
        api_response = api_instance.undelete_owner(undelete_owner_parameters)
        print("The response of OwnersApi->undelete_owner:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling OwnersApi->undelete_owner: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **undelete_owner_parameters** | [**UndeleteOwnerParameters**](UndeleteOwnerParameters.md)|  | 

### Return type

[**OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

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

