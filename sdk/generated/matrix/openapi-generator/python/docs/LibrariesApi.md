# grace_generated_openapi_probe.LibrariesApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**add_library**](LibrariesApi.md#add_library) | **POST** /libraries/add | Add one empty normalized Library under an exact configuration version.
[**continue_library_bootstrap**](LibrariesApi.md#continue_library_bootstrap) | **POST** /libraries/bootstrap/continue | Continue one immutable bootstrap baseline page sequence.
[**download_library_content**](LibrariesApi.md#download_library_content) | **GET** /libraries/content/{grantId} | Redeem one authorized short-lived immutable-content read grant.
[**get_library_catalog**](LibrariesApi.md#get_library_catalog) | **POST** /libraries/catalog/get | Get the persisted Library configuration.
[**get_library_changes**](LibrariesApi.md#get_library_changes) | **POST** /libraries/changes/get | Read repository-ordered accepted Library changes after an opaque cursor.
[**get_library_item**](LibrariesApi.md#get_library_item) | **POST** /libraries/items/get | Get one current Library item.
[**get_library_namespace_slot**](LibrariesApi.md#get_library_namespace_slot) | **POST** /libraries/namespace/get-slot | Get one current occupied or remembered-vacant Library namespace slot.
[**get_library_operation**](LibrariesApi.md#get_library_operation) | **POST** /libraries/operations/get | Get the stable receipt for one authorized Library operation identity.
[**get_library_status**](LibrariesApi.md#get_library_status) | **POST** /libraries/status/get | Get content-free Library repository status.
[**list_libraries**](LibrariesApi.md#list_libraries) | **POST** /libraries/list | List the sorted Libraries and their exact configuration version.
[**prepare_library_content**](LibrariesApi.md#prepare_library_content) | **POST** /libraries/content/prepare | Prepare exact immutable bytes for a later Library change.
[**prepare_library_content_read**](LibrariesApi.md#prepare_library_content_read) | **POST** /libraries/content/read | Prepare a one-use read grant for an authorized retained content version.
[**remove_library**](LibrariesApi.md#remove_library) | **POST** /libraries/remove | Remove one empty normalized Library under an exact configuration version.
[**start_library_bootstrap**](LibrariesApi.md#start_library_bootstrap) | **POST** /libraries/bootstrap/start | Start a bounded bootstrap from the current immutable baseline.
[**submit_library_change**](LibrariesApi.md#submit_library_change) | **POST** /libraries/changes/submit | Submit one exact idempotent Library namespace or content change.


# **add_library**
> LibraryCatalogChangeReturnValue add_library(add_library_parameters)

Add one empty normalized Library under an exact configuration version.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.add_library_parameters import AddLibraryParameters
from grace_generated_openapi_probe.models.library_catalog_change_return_value import LibraryCatalogChangeReturnValue
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    add_library_parameters = grace_generated_openapi_probe.AddLibraryParameters() # AddLibraryParameters | 

    try:
        # Add one empty normalized Library under an exact configuration version.
        api_response = api_instance.add_library(add_library_parameters)
        print("The response of LibrariesApi->add_library:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->add_library: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **add_library_parameters** | [**AddLibraryParameters**](AddLibraryParameters.md)|  | 

### Return type

[**LibraryCatalogChangeReturnValue**](LibraryCatalogChangeReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Exact-version Library change result. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**409** | Conflict |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **continue_library_bootstrap**
> LibraryBootstrapPageReturnValue continue_library_bootstrap(continue_library_bootstrap_parameters)

Continue one immutable bootstrap baseline page sequence.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.continue_library_bootstrap_parameters import ContinueLibraryBootstrapParameters
from grace_generated_openapi_probe.models.library_bootstrap_page_return_value import LibraryBootstrapPageReturnValue
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    continue_library_bootstrap_parameters = grace_generated_openapi_probe.ContinueLibraryBootstrapParameters() # ContinueLibraryBootstrapParameters | 

    try:
        # Continue one immutable bootstrap baseline page sequence.
        api_response = api_instance.continue_library_bootstrap(continue_library_bootstrap_parameters)
        print("The response of LibrariesApi->continue_library_bootstrap:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->continue_library_bootstrap: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **continue_library_bootstrap_parameters** | [**ContinueLibraryBootstrapParameters**](ContinueLibraryBootstrapParameters.md)|  | 

### Return type

[**LibraryBootstrapPageReturnValue**](LibraryBootstrapPageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | One immutable bootstrap baseline page. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**404** | Not Found |  -  |
**410** | Gone |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **download_library_content**
> bytes download_library_content(grant_id)

Redeem one authorized short-lived immutable-content read grant.

### Example


```python
import grace_generated_openapi_probe
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    grant_id = 'grant_id_example' # str | 

    try:
        # Redeem one authorized short-lived immutable-content read grant.
        api_response = api_instance.download_library_content(grant_id)
        print("The response of LibrariesApi->download_library_content:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->download_library_content: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **grant_id** | **str**|  | 

### Return type

**bytes**

### Authorization

No authorization required

### HTTP request headers

 - **Content-Type**: Not defined
 - **Accept**: application/octet-stream, application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Exact immutable bytes authorized by the one-use grant. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**404** | Not Found |  -  |
**410** | Gone |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_library_catalog**
> LibraryCatalogReturnValue get_library_catalog(get_library_catalog_parameters)

Get the persisted Library configuration.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_library_catalog_parameters import GetLibraryCatalogParameters
from grace_generated_openapi_probe.models.library_catalog_return_value import LibraryCatalogReturnValue
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    get_library_catalog_parameters = grace_generated_openapi_probe.GetLibraryCatalogParameters() # GetLibraryCatalogParameters | 

    try:
        # Get the persisted Library configuration.
        api_response = api_instance.get_library_catalog(get_library_catalog_parameters)
        print("The response of LibrariesApi->get_library_catalog:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->get_library_catalog: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_library_catalog_parameters** | [**GetLibraryCatalogParameters**](GetLibraryCatalogParameters.md)|  | 

### Return type

[**LibraryCatalogReturnValue**](LibraryCatalogReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Current persisted Library configuration. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_library_changes**
> LibraryChangePageReturnValue get_library_changes(get_library_changes_parameters)

Read repository-ordered accepted Library changes after an opaque cursor.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_library_changes_parameters import GetLibraryChangesParameters
from grace_generated_openapi_probe.models.library_change_page_return_value import LibraryChangePageReturnValue
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    get_library_changes_parameters = grace_generated_openapi_probe.GetLibraryChangesParameters() # GetLibraryChangesParameters | 

    try:
        # Read repository-ordered accepted Library changes after an opaque cursor.
        api_response = api_instance.get_library_changes(get_library_changes_parameters)
        print("The response of LibrariesApi->get_library_changes:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->get_library_changes: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_library_changes_parameters** | [**GetLibraryChangesParameters**](GetLibraryChangesParameters.md)|  | 

### Return type

[**LibraryChangePageReturnValue**](LibraryChangePageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Ordered accepted changes or a rebaseline instruction. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_library_item**
> LibraryItemReturnValue get_library_item(get_library_item_parameters)

Get one current Library item.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_library_item_parameters import GetLibraryItemParameters
from grace_generated_openapi_probe.models.library_item_return_value import LibraryItemReturnValue
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    get_library_item_parameters = grace_generated_openapi_probe.GetLibraryItemParameters() # GetLibraryItemParameters | 

    try:
        # Get one current Library item.
        api_response = api_instance.get_library_item(get_library_item_parameters)
        print("The response of LibrariesApi->get_library_item:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->get_library_item: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_library_item_parameters** | [**GetLibraryItemParameters**](GetLibraryItemParameters.md)|  | 

### Return type

[**LibraryItemReturnValue**](LibraryItemReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Current Library item state. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**404** | Not Found |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_library_namespace_slot**
> LibraryNamespaceSlotReturnValue get_library_namespace_slot(get_library_namespace_slot_parameters)

Get one current occupied or remembered-vacant Library namespace slot.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_library_namespace_slot_parameters import GetLibraryNamespaceSlotParameters
from grace_generated_openapi_probe.models.library_namespace_slot_return_value import LibraryNamespaceSlotReturnValue
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    get_library_namespace_slot_parameters = grace_generated_openapi_probe.GetLibraryNamespaceSlotParameters() # GetLibraryNamespaceSlotParameters | 

    try:
        # Get one current occupied or remembered-vacant Library namespace slot.
        api_response = api_instance.get_library_namespace_slot(get_library_namespace_slot_parameters)
        print("The response of LibrariesApi->get_library_namespace_slot:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->get_library_namespace_slot: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_library_namespace_slot_parameters** | [**GetLibraryNamespaceSlotParameters**](GetLibraryNamespaceSlotParameters.md)|  | 

### Return type

[**LibraryNamespaceSlotReturnValue**](LibraryNamespaceSlotReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Current occupied or remembered-vacant namespace slot. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_library_operation**
> LibraryOperationReceiptReturnValue get_library_operation(get_library_operation_parameters)

Get the stable receipt for one authorized Library operation identity.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_library_operation_parameters import GetLibraryOperationParameters
from grace_generated_openapi_probe.models.library_operation_receipt_return_value import LibraryOperationReceiptReturnValue
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    get_library_operation_parameters = grace_generated_openapi_probe.GetLibraryOperationParameters() # GetLibraryOperationParameters | 

    try:
        # Get the stable receipt for one authorized Library operation identity.
        api_response = api_instance.get_library_operation(get_library_operation_parameters)
        print("The response of LibrariesApi->get_library_operation:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->get_library_operation: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_library_operation_parameters** | [**GetLibraryOperationParameters**](GetLibraryOperationParameters.md)|  | 

### Return type

[**LibraryOperationReceiptReturnValue**](LibraryOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Stable Library operation receipt. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**404** | Not Found |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_library_status**
> LibraryStatusReturnValue get_library_status(get_library_status_parameters)

Get content-free Library repository status.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_library_status_parameters import GetLibraryStatusParameters
from grace_generated_openapi_probe.models.library_status_return_value import LibraryStatusReturnValue
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    get_library_status_parameters = grace_generated_openapi_probe.GetLibraryStatusParameters() # GetLibraryStatusParameters | 

    try:
        # Get content-free Library repository status.
        api_response = api_instance.get_library_status(get_library_status_parameters)
        print("The response of LibrariesApi->get_library_status:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->get_library_status: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_library_status_parameters** | [**GetLibraryStatusParameters**](GetLibraryStatusParameters.md)|  | 

### Return type

[**LibraryStatusReturnValue**](LibraryStatusReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Content-free Library repository status. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **list_libraries**
> LibraryCatalogReturnValue list_libraries(list_libraries_parameters)

List the sorted Libraries and their exact configuration version.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.library_catalog_return_value import LibraryCatalogReturnValue
from grace_generated_openapi_probe.models.list_libraries_parameters import ListLibrariesParameters
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    list_libraries_parameters = grace_generated_openapi_probe.ListLibrariesParameters() # ListLibrariesParameters | 

    try:
        # List the sorted Libraries and their exact configuration version.
        api_response = api_instance.list_libraries(list_libraries_parameters)
        print("The response of LibrariesApi->list_libraries:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->list_libraries: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **list_libraries_parameters** | [**ListLibrariesParameters**](ListLibrariesParameters.md)|  | 

### Return type

[**LibraryCatalogReturnValue**](LibraryCatalogReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Current persisted Library configuration. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **prepare_library_content**
> LibraryPreparedContentReturnValue prepare_library_content(prepare_library_content_parameters)

Prepare exact immutable bytes for a later Library change.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.library_prepared_content_return_value import LibraryPreparedContentReturnValue
from grace_generated_openapi_probe.models.prepare_library_content_parameters import PrepareLibraryContentParameters
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    prepare_library_content_parameters = grace_generated_openapi_probe.PrepareLibraryContentParameters() # PrepareLibraryContentParameters | 

    try:
        # Prepare exact immutable bytes for a later Library change.
        api_response = api_instance.prepare_library_content(prepare_library_content_parameters)
        print("The response of LibrariesApi->prepare_library_content:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->prepare_library_content: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **prepare_library_content_parameters** | [**PrepareLibraryContentParameters**](PrepareLibraryContentParameters.md)|  | 

### Return type

[**LibraryPreparedContentReturnValue**](LibraryPreparedContentReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Authorized immutable-content preparation. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**409** | Conflict |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **prepare_library_content_read**
> LibraryContentReadGrantReturnValue prepare_library_content_read(prepare_library_content_read_parameters)

Prepare a one-use read grant for an authorized retained content version.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.library_content_read_grant_return_value import LibraryContentReadGrantReturnValue
from grace_generated_openapi_probe.models.prepare_library_content_read_parameters import PrepareLibraryContentReadParameters
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    prepare_library_content_read_parameters = grace_generated_openapi_probe.PrepareLibraryContentReadParameters() # PrepareLibraryContentReadParameters | 

    try:
        # Prepare a one-use read grant for an authorized retained content version.
        api_response = api_instance.prepare_library_content_read(prepare_library_content_read_parameters)
        print("The response of LibrariesApi->prepare_library_content_read:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->prepare_library_content_read: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **prepare_library_content_read_parameters** | [**PrepareLibraryContentReadParameters**](PrepareLibraryContentReadParameters.md)|  | 

### Return type

[**LibraryContentReadGrantReturnValue**](LibraryContentReadGrantReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | One-use authorized immutable-content read grant. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**404** | Not Found |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **remove_library**
> LibraryCatalogChangeReturnValue remove_library(remove_library_parameters)

Remove one empty normalized Library under an exact configuration version.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.library_catalog_change_return_value import LibraryCatalogChangeReturnValue
from grace_generated_openapi_probe.models.remove_library_parameters import RemoveLibraryParameters
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    remove_library_parameters = grace_generated_openapi_probe.RemoveLibraryParameters() # RemoveLibraryParameters | 

    try:
        # Remove one empty normalized Library under an exact configuration version.
        api_response = api_instance.remove_library(remove_library_parameters)
        print("The response of LibrariesApi->remove_library:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->remove_library: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **remove_library_parameters** | [**RemoveLibraryParameters**](RemoveLibraryParameters.md)|  | 

### Return type

[**LibraryCatalogChangeReturnValue**](LibraryCatalogChangeReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Exact-version Library change result. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**409** | Conflict |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **start_library_bootstrap**
> LibraryBootstrapPageReturnValue start_library_bootstrap(start_library_bootstrap_parameters)

Start a bounded bootstrap from the current immutable baseline.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.library_bootstrap_page_return_value import LibraryBootstrapPageReturnValue
from grace_generated_openapi_probe.models.start_library_bootstrap_parameters import StartLibraryBootstrapParameters
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    start_library_bootstrap_parameters = grace_generated_openapi_probe.StartLibraryBootstrapParameters() # StartLibraryBootstrapParameters | 

    try:
        # Start a bounded bootstrap from the current immutable baseline.
        api_response = api_instance.start_library_bootstrap(start_library_bootstrap_parameters)
        print("The response of LibrariesApi->start_library_bootstrap:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->start_library_bootstrap: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **start_library_bootstrap_parameters** | [**StartLibraryBootstrapParameters**](StartLibraryBootstrapParameters.md)|  | 

### Return type

[**LibraryBootstrapPageReturnValue**](LibraryBootstrapPageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | One immutable bootstrap baseline page. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **submit_library_change**
> LibraryOperationReceiptReturnValue submit_library_change(submit_library_change_parameters)

Submit one exact idempotent Library namespace or content change.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.library_operation_receipt_return_value import LibraryOperationReceiptReturnValue
from grace_generated_openapi_probe.models.submit_library_change_parameters import SubmitLibraryChangeParameters
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
    api_instance = grace_generated_openapi_probe.LibrariesApi(api_client)
    submit_library_change_parameters = grace_generated_openapi_probe.SubmitLibraryChangeParameters() # SubmitLibraryChangeParameters | 

    try:
        # Submit one exact idempotent Library namespace or content change.
        api_response = api_instance.submit_library_change(submit_library_change_parameters)
        print("The response of LibrariesApi->submit_library_change:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling LibrariesApi->submit_library_change: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **submit_library_change_parameters** | [**SubmitLibraryChangeParameters**](SubmitLibraryChangeParameters.md)|  | 

### Return type

[**LibraryOperationReceiptReturnValue**](LibraryOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Stable Library operation receipt. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**409** | Conflict |  -  |
**410** | Gone |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

