# grace_generated_openapi_probe.SynchronizedContentApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**add_synchronized_root**](SynchronizedContentApi.md#add_synchronized_root) | **POST** /sync/roots/add | Add one empty normalized synchronized root under an exact configuration version.
[**continue_synchronized_bootstrap**](SynchronizedContentApi.md#continue_synchronized_bootstrap) | **POST** /sync/bootstrap/continue | Continue one immutable bootstrap baseline page sequence.
[**download_synchronized_content**](SynchronizedContentApi.md#download_synchronized_content) | **GET** /sync/content/{grantId} | Redeem one authorized short-lived immutable-content read grant.
[**get_synchronized_deltas**](SynchronizedContentApi.md#get_synchronized_deltas) | **POST** /sync/deltas/get | Read repository-ordered accepted synchronized mutations after an opaque cursor.
[**get_synchronized_item**](SynchronizedContentApi.md#get_synchronized_item) | **POST** /sync/items/get | Get one current synchronized item.
[**get_synchronized_namespace_slot**](SynchronizedContentApi.md#get_synchronized_namespace_slot) | **POST** /sync/namespace/get-slot | Get one current occupied or remembered-vacant synchronized namespace slot.
[**get_synchronized_operation**](SynchronizedContentApi.md#get_synchronized_operation) | **POST** /sync/operations/get | Get the stable receipt for one authorized synchronized operation identity.
[**get_synchronized_root_configuration**](SynchronizedContentApi.md#get_synchronized_root_configuration) | **POST** /sync/roots/get | Get the persisted synchronized-root configuration.
[**get_synchronized_status**](SynchronizedContentApi.md#get_synchronized_status) | **POST** /sync/status/get | Get content-free synchronized repository status.
[**list_synchronized_roots**](SynchronizedContentApi.md#list_synchronized_roots) | **POST** /sync/roots/list | List the sorted synchronized roots and their exact configuration version.
[**prepare_synchronized_content**](SynchronizedContentApi.md#prepare_synchronized_content) | **POST** /sync/content/prepare | Prepare exact immutable bytes for a later synchronized mutation.
[**prepare_synchronized_content_read**](SynchronizedContentApi.md#prepare_synchronized_content_read) | **POST** /sync/content/read | Prepare a one-use read grant for an authorized retained content version.
[**remove_synchronized_root**](SynchronizedContentApi.md#remove_synchronized_root) | **POST** /sync/roots/remove | Remove one empty normalized synchronized root under an exact configuration version.
[**start_synchronized_bootstrap**](SynchronizedContentApi.md#start_synchronized_bootstrap) | **POST** /sync/bootstrap/start | Start a bounded bootstrap from the current immutable baseline.
[**submit_synchronized_mutation**](SynchronizedContentApi.md#submit_synchronized_mutation) | **POST** /sync/mutations/submit | Submit one exact idempotent synchronized namespace or content mutation.


# **add_synchronized_root**
> SynchronizedRootMutationReturnValue add_synchronized_root(add_synchronized_root_parameters)

Add one empty normalized synchronized root under an exact configuration version.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.add_synchronized_root_parameters import AddSynchronizedRootParameters
from grace_generated_openapi_probe.models.synchronized_root_mutation_return_value import SynchronizedRootMutationReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    add_synchronized_root_parameters = grace_generated_openapi_probe.AddSynchronizedRootParameters() # AddSynchronizedRootParameters | 

    try:
        # Add one empty normalized synchronized root under an exact configuration version.
        api_response = api_instance.add_synchronized_root(add_synchronized_root_parameters)
        print("The response of SynchronizedContentApi->add_synchronized_root:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->add_synchronized_root: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **add_synchronized_root_parameters** | [**AddSynchronizedRootParameters**](AddSynchronizedRootParameters.md)|  | 

### Return type

[**SynchronizedRootMutationReturnValue**](SynchronizedRootMutationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Exact-version synchronized-root mutation result. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**409** | Conflict |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **continue_synchronized_bootstrap**
> SynchronizedBootstrapPageReturnValue continue_synchronized_bootstrap(continue_synchronized_bootstrap_parameters)

Continue one immutable bootstrap baseline page sequence.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.continue_synchronized_bootstrap_parameters import ContinueSynchronizedBootstrapParameters
from grace_generated_openapi_probe.models.synchronized_bootstrap_page_return_value import SynchronizedBootstrapPageReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    continue_synchronized_bootstrap_parameters = grace_generated_openapi_probe.ContinueSynchronizedBootstrapParameters() # ContinueSynchronizedBootstrapParameters | 

    try:
        # Continue one immutable bootstrap baseline page sequence.
        api_response = api_instance.continue_synchronized_bootstrap(continue_synchronized_bootstrap_parameters)
        print("The response of SynchronizedContentApi->continue_synchronized_bootstrap:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->continue_synchronized_bootstrap: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **continue_synchronized_bootstrap_parameters** | [**ContinueSynchronizedBootstrapParameters**](ContinueSynchronizedBootstrapParameters.md)|  | 

### Return type

[**SynchronizedBootstrapPageReturnValue**](SynchronizedBootstrapPageReturnValue.md)

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

# **download_synchronized_content**
> bytes download_synchronized_content(grant_id)

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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    grant_id = 'grant_id_example' # str | 

    try:
        # Redeem one authorized short-lived immutable-content read grant.
        api_response = api_instance.download_synchronized_content(grant_id)
        print("The response of SynchronizedContentApi->download_synchronized_content:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->download_synchronized_content: %s\n" % e)
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

# **get_synchronized_deltas**
> SynchronizedDeltaReturnValue get_synchronized_deltas(get_synchronized_deltas_parameters)

Read repository-ordered accepted synchronized mutations after an opaque cursor.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_synchronized_deltas_parameters import GetSynchronizedDeltasParameters
from grace_generated_openapi_probe.models.synchronized_delta_return_value import SynchronizedDeltaReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    get_synchronized_deltas_parameters = grace_generated_openapi_probe.GetSynchronizedDeltasParameters() # GetSynchronizedDeltasParameters | 

    try:
        # Read repository-ordered accepted synchronized mutations after an opaque cursor.
        api_response = api_instance.get_synchronized_deltas(get_synchronized_deltas_parameters)
        print("The response of SynchronizedContentApi->get_synchronized_deltas:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->get_synchronized_deltas: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_synchronized_deltas_parameters** | [**GetSynchronizedDeltasParameters**](GetSynchronizedDeltasParameters.md)|  | 

### Return type

[**SynchronizedDeltaReturnValue**](SynchronizedDeltaReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Ordered accepted mutations or a rebaseline instruction. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_synchronized_item**
> SynchronizedItemReturnValue get_synchronized_item(get_synchronized_item_parameters)

Get one current synchronized item.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_synchronized_item_parameters import GetSynchronizedItemParameters
from grace_generated_openapi_probe.models.synchronized_item_return_value import SynchronizedItemReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    get_synchronized_item_parameters = grace_generated_openapi_probe.GetSynchronizedItemParameters() # GetSynchronizedItemParameters | 

    try:
        # Get one current synchronized item.
        api_response = api_instance.get_synchronized_item(get_synchronized_item_parameters)
        print("The response of SynchronizedContentApi->get_synchronized_item:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->get_synchronized_item: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_synchronized_item_parameters** | [**GetSynchronizedItemParameters**](GetSynchronizedItemParameters.md)|  | 

### Return type

[**SynchronizedItemReturnValue**](SynchronizedItemReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Current synchronized item state. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**404** | Not Found |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_synchronized_namespace_slot**
> SynchronizedNamespaceSlotReturnValue get_synchronized_namespace_slot(get_synchronized_namespace_slot_parameters)

Get one current occupied or remembered-vacant synchronized namespace slot.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_synchronized_namespace_slot_parameters import GetSynchronizedNamespaceSlotParameters
from grace_generated_openapi_probe.models.synchronized_namespace_slot_return_value import SynchronizedNamespaceSlotReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    get_synchronized_namespace_slot_parameters = grace_generated_openapi_probe.GetSynchronizedNamespaceSlotParameters() # GetSynchronizedNamespaceSlotParameters | 

    try:
        # Get one current occupied or remembered-vacant synchronized namespace slot.
        api_response = api_instance.get_synchronized_namespace_slot(get_synchronized_namespace_slot_parameters)
        print("The response of SynchronizedContentApi->get_synchronized_namespace_slot:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->get_synchronized_namespace_slot: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_synchronized_namespace_slot_parameters** | [**GetSynchronizedNamespaceSlotParameters**](GetSynchronizedNamespaceSlotParameters.md)|  | 

### Return type

[**SynchronizedNamespaceSlotReturnValue**](SynchronizedNamespaceSlotReturnValue.md)

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

# **get_synchronized_operation**
> SynchronizedOperationReceiptReturnValue get_synchronized_operation(get_synchronized_operation_parameters)

Get the stable receipt for one authorized synchronized operation identity.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_synchronized_operation_parameters import GetSynchronizedOperationParameters
from grace_generated_openapi_probe.models.synchronized_operation_receipt_return_value import SynchronizedOperationReceiptReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    get_synchronized_operation_parameters = grace_generated_openapi_probe.GetSynchronizedOperationParameters() # GetSynchronizedOperationParameters | 

    try:
        # Get the stable receipt for one authorized synchronized operation identity.
        api_response = api_instance.get_synchronized_operation(get_synchronized_operation_parameters)
        print("The response of SynchronizedContentApi->get_synchronized_operation:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->get_synchronized_operation: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_synchronized_operation_parameters** | [**GetSynchronizedOperationParameters**](GetSynchronizedOperationParameters.md)|  | 

### Return type

[**SynchronizedOperationReceiptReturnValue**](SynchronizedOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Stable synchronized operation receipt. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**404** | Not Found |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_synchronized_root_configuration**
> SynchronizedRootConfigurationReturnValue get_synchronized_root_configuration(get_synchronized_root_configuration_parameters)

Get the persisted synchronized-root configuration.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_synchronized_root_configuration_parameters import GetSynchronizedRootConfigurationParameters
from grace_generated_openapi_probe.models.synchronized_root_configuration_return_value import SynchronizedRootConfigurationReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    get_synchronized_root_configuration_parameters = grace_generated_openapi_probe.GetSynchronizedRootConfigurationParameters() # GetSynchronizedRootConfigurationParameters | 

    try:
        # Get the persisted synchronized-root configuration.
        api_response = api_instance.get_synchronized_root_configuration(get_synchronized_root_configuration_parameters)
        print("The response of SynchronizedContentApi->get_synchronized_root_configuration:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->get_synchronized_root_configuration: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_synchronized_root_configuration_parameters** | [**GetSynchronizedRootConfigurationParameters**](GetSynchronizedRootConfigurationParameters.md)|  | 

### Return type

[**SynchronizedRootConfigurationReturnValue**](SynchronizedRootConfigurationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Current persisted synchronized-root configuration. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_synchronized_status**
> SynchronizedStatusReturnValue get_synchronized_status(get_synchronized_status_parameters)

Get content-free synchronized repository status.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_synchronized_status_parameters import GetSynchronizedStatusParameters
from grace_generated_openapi_probe.models.synchronized_status_return_value import SynchronizedStatusReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    get_synchronized_status_parameters = grace_generated_openapi_probe.GetSynchronizedStatusParameters() # GetSynchronizedStatusParameters | 

    try:
        # Get content-free synchronized repository status.
        api_response = api_instance.get_synchronized_status(get_synchronized_status_parameters)
        print("The response of SynchronizedContentApi->get_synchronized_status:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->get_synchronized_status: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_synchronized_status_parameters** | [**GetSynchronizedStatusParameters**](GetSynchronizedStatusParameters.md)|  | 

### Return type

[**SynchronizedStatusReturnValue**](SynchronizedStatusReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Content-free synchronized repository status. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **list_synchronized_roots**
> SynchronizedRootConfigurationReturnValue list_synchronized_roots(list_synchronized_roots_parameters)

List the sorted synchronized roots and their exact configuration version.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.list_synchronized_roots_parameters import ListSynchronizedRootsParameters
from grace_generated_openapi_probe.models.synchronized_root_configuration_return_value import SynchronizedRootConfigurationReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    list_synchronized_roots_parameters = grace_generated_openapi_probe.ListSynchronizedRootsParameters() # ListSynchronizedRootsParameters | 

    try:
        # List the sorted synchronized roots and their exact configuration version.
        api_response = api_instance.list_synchronized_roots(list_synchronized_roots_parameters)
        print("The response of SynchronizedContentApi->list_synchronized_roots:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->list_synchronized_roots: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **list_synchronized_roots_parameters** | [**ListSynchronizedRootsParameters**](ListSynchronizedRootsParameters.md)|  | 

### Return type

[**SynchronizedRootConfigurationReturnValue**](SynchronizedRootConfigurationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Current persisted synchronized-root configuration. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **prepare_synchronized_content**
> SynchronizedPreparedContentReturnValue prepare_synchronized_content(prepare_synchronized_content_parameters)

Prepare exact immutable bytes for a later synchronized mutation.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.prepare_synchronized_content_parameters import PrepareSynchronizedContentParameters
from grace_generated_openapi_probe.models.synchronized_prepared_content_return_value import SynchronizedPreparedContentReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    prepare_synchronized_content_parameters = grace_generated_openapi_probe.PrepareSynchronizedContentParameters() # PrepareSynchronizedContentParameters | 

    try:
        # Prepare exact immutable bytes for a later synchronized mutation.
        api_response = api_instance.prepare_synchronized_content(prepare_synchronized_content_parameters)
        print("The response of SynchronizedContentApi->prepare_synchronized_content:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->prepare_synchronized_content: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **prepare_synchronized_content_parameters** | [**PrepareSynchronizedContentParameters**](PrepareSynchronizedContentParameters.md)|  | 

### Return type

[**SynchronizedPreparedContentReturnValue**](SynchronizedPreparedContentReturnValue.md)

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

# **prepare_synchronized_content_read**
> SynchronizedContentReadGrantReturnValue prepare_synchronized_content_read(prepare_synchronized_content_read_parameters)

Prepare a one-use read grant for an authorized retained content version.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.prepare_synchronized_content_read_parameters import PrepareSynchronizedContentReadParameters
from grace_generated_openapi_probe.models.synchronized_content_read_grant_return_value import SynchronizedContentReadGrantReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    prepare_synchronized_content_read_parameters = grace_generated_openapi_probe.PrepareSynchronizedContentReadParameters() # PrepareSynchronizedContentReadParameters | 

    try:
        # Prepare a one-use read grant for an authorized retained content version.
        api_response = api_instance.prepare_synchronized_content_read(prepare_synchronized_content_read_parameters)
        print("The response of SynchronizedContentApi->prepare_synchronized_content_read:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->prepare_synchronized_content_read: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **prepare_synchronized_content_read_parameters** | [**PrepareSynchronizedContentReadParameters**](PrepareSynchronizedContentReadParameters.md)|  | 

### Return type

[**SynchronizedContentReadGrantReturnValue**](SynchronizedContentReadGrantReturnValue.md)

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

# **remove_synchronized_root**
> SynchronizedRootMutationReturnValue remove_synchronized_root(remove_synchronized_root_parameters)

Remove one empty normalized synchronized root under an exact configuration version.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.remove_synchronized_root_parameters import RemoveSynchronizedRootParameters
from grace_generated_openapi_probe.models.synchronized_root_mutation_return_value import SynchronizedRootMutationReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    remove_synchronized_root_parameters = grace_generated_openapi_probe.RemoveSynchronizedRootParameters() # RemoveSynchronizedRootParameters | 

    try:
        # Remove one empty normalized synchronized root under an exact configuration version.
        api_response = api_instance.remove_synchronized_root(remove_synchronized_root_parameters)
        print("The response of SynchronizedContentApi->remove_synchronized_root:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->remove_synchronized_root: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **remove_synchronized_root_parameters** | [**RemoveSynchronizedRootParameters**](RemoveSynchronizedRootParameters.md)|  | 

### Return type

[**SynchronizedRootMutationReturnValue**](SynchronizedRootMutationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Exact-version synchronized-root mutation result. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**409** | Conflict |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **start_synchronized_bootstrap**
> SynchronizedBootstrapPageReturnValue start_synchronized_bootstrap(start_synchronized_bootstrap_parameters)

Start a bounded bootstrap from the current immutable baseline.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.start_synchronized_bootstrap_parameters import StartSynchronizedBootstrapParameters
from grace_generated_openapi_probe.models.synchronized_bootstrap_page_return_value import SynchronizedBootstrapPageReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    start_synchronized_bootstrap_parameters = grace_generated_openapi_probe.StartSynchronizedBootstrapParameters() # StartSynchronizedBootstrapParameters | 

    try:
        # Start a bounded bootstrap from the current immutable baseline.
        api_response = api_instance.start_synchronized_bootstrap(start_synchronized_bootstrap_parameters)
        print("The response of SynchronizedContentApi->start_synchronized_bootstrap:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->start_synchronized_bootstrap: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **start_synchronized_bootstrap_parameters** | [**StartSynchronizedBootstrapParameters**](StartSynchronizedBootstrapParameters.md)|  | 

### Return type

[**SynchronizedBootstrapPageReturnValue**](SynchronizedBootstrapPageReturnValue.md)

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

# **submit_synchronized_mutation**
> SynchronizedOperationReceiptReturnValue submit_synchronized_mutation(submit_synchronized_mutation_parameters)

Submit one exact idempotent synchronized namespace or content mutation.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.submit_synchronized_mutation_parameters import SubmitSynchronizedMutationParameters
from grace_generated_openapi_probe.models.synchronized_operation_receipt_return_value import SynchronizedOperationReceiptReturnValue
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
    api_instance = grace_generated_openapi_probe.SynchronizedContentApi(api_client)
    submit_synchronized_mutation_parameters = grace_generated_openapi_probe.SubmitSynchronizedMutationParameters() # SubmitSynchronizedMutationParameters | 

    try:
        # Submit one exact idempotent synchronized namespace or content mutation.
        api_response = api_instance.submit_synchronized_mutation(submit_synchronized_mutation_parameters)
        print("The response of SynchronizedContentApi->submit_synchronized_mutation:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling SynchronizedContentApi->submit_synchronized_mutation: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **submit_synchronized_mutation_parameters** | [**SubmitSynchronizedMutationParameters**](SubmitSynchronizedMutationParameters.md)|  | 

### Return type

[**SynchronizedOperationReceiptReturnValue**](SynchronizedOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json, text/plain

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Stable synchronized operation receipt. |  -  |
**400** | Bad Request |  -  |
**403** | Forbidden |  -  |
**409** | Conflict |  -  |
**410** | Gone |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

