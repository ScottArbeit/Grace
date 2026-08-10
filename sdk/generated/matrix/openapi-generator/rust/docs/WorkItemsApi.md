# \WorkItemsApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**delete_work_item_attachment**](WorkItemsApi.md#delete_work_item_attachment) | **POST** /work/attachments/delete | Logically delete one owned work-item attachment.
[**set_work_item_description**](WorkItemsApi.md#set_work_item_description) | **POST** /work/description/set | Replace a work-item description.
[**undelete_work_item_attachment**](WorkItemsApi.md#undelete_work_item_attachment) | **POST** /work/attachments/undelete | Recover one logically deleted work-item attachment.



## delete_work_item_attachment

> models::InlineObject8 delete_work_item_attachment(delete_work_item_attachment_parameters)
Logically delete one owned work-item attachment.

Retains the blob, artifact state, and owning link until the stored repository-retention deadline.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**delete_work_item_attachment_parameters** | [**DeleteWorkItemAttachmentParameters**](DeleteWorkItemAttachmentParameters.md) |  | [required] |

### Return type

[**models::InlineObject8**](inline_object_8.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_work_item_description

> models::InlineObject9 set_work_item_description(set_work_item_description_parameters)
Replace a work-item description.

Stores a new immutable UTF-8 description and makes it the current description in append order.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_work_item_description_parameters** | [**SetWorkItemDescriptionParameters**](SetWorkItemDescriptionParameters.md) |  | [required] |

### Return type

[**models::InlineObject9**](inline_object_9.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## undelete_work_item_attachment

> models::InlineObject9 undelete_work_item_attachment(undelete_work_item_attachment_parameters)
Recover one logically deleted work-item attachment.

Restores the attachment before its immutable physical-cleanup deadline.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**undelete_work_item_attachment_parameters** | [**UndeleteWorkItemAttachmentParameters**](UndeleteWorkItemAttachmentParameters.md) |  | [required] |

### Return type

[**models::InlineObject9**](inline_object_9.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

