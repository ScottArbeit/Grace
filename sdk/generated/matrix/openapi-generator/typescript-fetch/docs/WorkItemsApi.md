# WorkItemsApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**deleteWorkItemAttachment**](WorkItemsApi.md#deleteworkitemattachment) | **POST** /work/attachments/delete | Logically delete one owned work-item attachment. |
| [**undeleteWorkItemAttachment**](WorkItemsApi.md#undeleteworkitemattachment) | **POST** /work/attachments/undelete | Recover one logically deleted work-item attachment. |



## deleteWorkItemAttachment

> InlineObject8 deleteWorkItemAttachment(deleteWorkItemAttachmentParameters)

Logically delete one owned work-item attachment.

Retains the blob, artifact state, and owning link until the stored repository-retention deadline.

### Example

```ts
import {
  Configuration,
  WorkItemsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DeleteWorkItemAttachmentRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WorkItemsApi(config);

  const body = {
    // DeleteWorkItemAttachmentParameters
    deleteWorkItemAttachmentParameters: ...,
  } satisfies DeleteWorkItemAttachmentRequest;

  try {
    const data = await api.deleteWorkItemAttachment(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **deleteWorkItemAttachmentParameters** | [DeleteWorkItemAttachmentParameters](DeleteWorkItemAttachmentParameters.md) |  | |

### Return type

[**InlineObject8**](InlineObject8.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | Attachment deletion accepted with a recoverable cleanup deadline. |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## undeleteWorkItemAttachment

> InlineObject9 undeleteWorkItemAttachment(undeleteWorkItemAttachmentParameters)

Recover one logically deleted work-item attachment.

Restores the attachment before its immutable physical-cleanup deadline.

### Example

```ts
import {
  Configuration,
  WorkItemsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { UndeleteWorkItemAttachmentRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WorkItemsApi(config);

  const body = {
    // UndeleteWorkItemAttachmentParameters
    undeleteWorkItemAttachmentParameters: ...,
  } satisfies UndeleteWorkItemAttachmentRequest;

  try {
    const data = await api.undeleteWorkItemAttachment(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **undeleteWorkItemAttachmentParameters** | [UndeleteWorkItemAttachmentParameters](UndeleteWorkItemAttachmentParameters.md) |  | |

### Return type

[**InlineObject9**](InlineObject9.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)

