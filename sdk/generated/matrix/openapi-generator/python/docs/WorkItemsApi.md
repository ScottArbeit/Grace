# grace_generated_openapi_probe.WorkItemsApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**clear_work_item_description**](WorkItemsApi.md#clear_work_item_description) | **POST** /work/description/clear | Clear a work-item description.
[**delete_work_item_attachment**](WorkItemsApi.md#delete_work_item_attachment) | **POST** /work/attachments/delete | Logically delete one owned work-item attachment.
[**set_work_item_description**](WorkItemsApi.md#set_work_item_description) | **POST** /work/description/set | Replace a work-item description.
[**undelete_work_item_attachment**](WorkItemsApi.md#undelete_work_item_attachment) | **POST** /work/attachments/undelete | Recover one logically deleted work-item attachment.


# **clear_work_item_description**
> InlineObject9 clear_work_item_description(clear_work_item_description_parameters)

Clear a work-item description.

Appends a new immutable empty description without deleting or exposing prior description history.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.clear_work_item_description_parameters import ClearWorkItemDescriptionParameters
from grace_generated_openapi_probe.models.inline_object9 import InlineObject9
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
    api_instance = grace_generated_openapi_probe.WorkItemsApi(api_client)
    clear_work_item_description_parameters = grace_generated_openapi_probe.ClearWorkItemDescriptionParameters() # ClearWorkItemDescriptionParameters | 

    try:
        # Clear a work-item description.
        api_response = api_instance.clear_work_item_description(clear_work_item_description_parameters)
        print("The response of WorkItemsApi->clear_work_item_description:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WorkItemsApi->clear_work_item_description: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **clear_work_item_description_parameters** | [**ClearWorkItemDescriptionParameters**](ClearWorkItemDescriptionParameters.md)|  | 

### Return type

[**InlineObject9**](InlineObject9.md)

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

# **delete_work_item_attachment**
> InlineObject8 delete_work_item_attachment(delete_work_item_attachment_parameters)

Logically delete one owned work-item attachment.

Retains the blob, artifact state, and owning link until the stored repository-retention deadline.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.delete_work_item_attachment_parameters import DeleteWorkItemAttachmentParameters
from grace_generated_openapi_probe.models.inline_object8 import InlineObject8
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
    api_instance = grace_generated_openapi_probe.WorkItemsApi(api_client)
    delete_work_item_attachment_parameters = grace_generated_openapi_probe.DeleteWorkItemAttachmentParameters() # DeleteWorkItemAttachmentParameters | 

    try:
        # Logically delete one owned work-item attachment.
        api_response = api_instance.delete_work_item_attachment(delete_work_item_attachment_parameters)
        print("The response of WorkItemsApi->delete_work_item_attachment:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WorkItemsApi->delete_work_item_attachment: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **delete_work_item_attachment_parameters** | [**DeleteWorkItemAttachmentParameters**](DeleteWorkItemAttachmentParameters.md)|  | 

### Return type

[**InlineObject8**](InlineObject8.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | Attachment deletion accepted with a recoverable cleanup deadline. |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **set_work_item_description**
> InlineObject9 set_work_item_description(set_work_item_description_parameters)

Replace a work-item description.

Stores a new immutable UTF-8 description and makes it the current description in append order.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object9 import InlineObject9
from grace_generated_openapi_probe.models.set_work_item_description_parameters import SetWorkItemDescriptionParameters
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
    api_instance = grace_generated_openapi_probe.WorkItemsApi(api_client)
    set_work_item_description_parameters = grace_generated_openapi_probe.SetWorkItemDescriptionParameters() # SetWorkItemDescriptionParameters | 

    try:
        # Replace a work-item description.
        api_response = api_instance.set_work_item_description(set_work_item_description_parameters)
        print("The response of WorkItemsApi->set_work_item_description:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WorkItemsApi->set_work_item_description: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **set_work_item_description_parameters** | [**SetWorkItemDescriptionParameters**](SetWorkItemDescriptionParameters.md)|  | 

### Return type

[**InlineObject9**](InlineObject9.md)

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

# **undelete_work_item_attachment**
> InlineObject9 undelete_work_item_attachment(undelete_work_item_attachment_parameters)

Recover one logically deleted work-item attachment.

Restores the attachment before its immutable physical-cleanup deadline.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object9 import InlineObject9
from grace_generated_openapi_probe.models.undelete_work_item_attachment_parameters import UndeleteWorkItemAttachmentParameters
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
    api_instance = grace_generated_openapi_probe.WorkItemsApi(api_client)
    undelete_work_item_attachment_parameters = grace_generated_openapi_probe.UndeleteWorkItemAttachmentParameters() # UndeleteWorkItemAttachmentParameters | 

    try:
        # Recover one logically deleted work-item attachment.
        api_response = api_instance.undelete_work_item_attachment(undelete_work_item_attachment_parameters)
        print("The response of WorkItemsApi->undelete_work_item_attachment:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WorkItemsApi->undelete_work_item_attachment: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **undelete_work_item_attachment_parameters** | [**UndeleteWorkItemAttachmentParameters**](UndeleteWorkItemAttachmentParameters.md)|  | 

### Return type

[**InlineObject9**](InlineObject9.md)

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

