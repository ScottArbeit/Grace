# ApprovalsApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**approvalRequestHistory**](ApprovalsApi.md#approvalrequesthistory) | **POST** /approval/request/history | Show approval request history. |
| [**approveApprovalRequest**](ApprovalsApi.md#approveapprovalrequest) | **POST** /approval/request/approve | Approve a workflow-generated approval request. |
| [**createApprovalPolicy**](ApprovalsApi.md#createapprovalpolicy) | **POST** /approval/policy/create | Create an approval policy. |
| [**deleteApprovalPolicy**](ApprovalsApi.md#deleteapprovalpolicy) | **POST** /approval/policy/delete | Delete an approval policy. |
| [**disableApprovalPolicy**](ApprovalsApi.md#disableapprovalpolicy) | **POST** /approval/policy/disable | Disable an approval policy. |
| [**enableApprovalPolicy**](ApprovalsApi.md#enableapprovalpolicy) | **POST** /approval/policy/enable | Enable an approval policy. |
| [**evaluateApprovalPolicy**](ApprovalsApi.md#evaluateapprovalpolicy) | **POST** /approval/policy/evaluate | Evaluate approval policies for a subject. |
| [**listApprovalPolicies**](ApprovalsApi.md#listapprovalpolicies) | **POST** /approval/policy/list | List approval policies. |
| [**listApprovalRequests**](ApprovalsApi.md#listapprovalrequests) | **POST** /approval/request/list | List workflow-generated approval requests. |
| [**rejectApprovalRequest**](ApprovalsApi.md#rejectapprovalrequest) | **POST** /approval/request/reject | Reject a workflow-generated approval request. |
| [**showApprovalPolicy**](ApprovalsApi.md#showapprovalpolicy) | **POST** /approval/policy/show | Show an approval policy. |
| [**showApprovalRequest**](ApprovalsApi.md#showapprovalrequest) | **POST** /approval/request/show | Show a workflow-generated approval request. |
| [**updateApprovalPolicy**](ApprovalsApi.md#updateapprovalpolicy) | **POST** /approval/policy/update | Update an approval policy. |



## approvalRequestHistory

> InlineObject2 approvalRequestHistory(approvalRequestParameters)

Show approval request history.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ApprovalRequestHistoryRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ApprovalRequestParameters
    approvalRequestParameters: ...,
  } satisfies ApprovalRequestHistoryRequest;

  try {
    const data = await api.approvalRequestHistory(body);
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
| **approvalRequestParameters** | [ApprovalRequestParameters](ApprovalRequestParameters.md) |  | |

### Return type

[**InlineObject2**](InlineObject2.md)

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


## approveApprovalRequest

> InlineObject3 approveApprovalRequest(approveApprovalRequestParameters)

Approve a workflow-generated approval request.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ApproveApprovalRequestRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ApproveApprovalRequestParameters
    approveApprovalRequestParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:bob","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","TargetBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","ApprovalRequestId":"5f9ed4df-d6df-4a65-a5d0-2830d62512c1","Reason":"Release criteria satisfied.","ClientDecisionId":"bob-approval-20260604-001"},
  } satisfies ApproveApprovalRequestRequest;

  try {
    const data = await api.approveApprovalRequest(body);
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
| **approveApprovalRequestParameters** | [ApproveApprovalRequestParameters](ApproveApprovalRequestParameters.md) |  | |

### Return type

[**InlineObject3**](InlineObject3.md)

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


## createApprovalPolicy

> InlineObject createApprovalPolicy(createApprovalPolicyParameters)

Create an approval policy.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { CreateApprovalPolicyRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // CreateApprovalPolicyParameters
    createApprovalPolicyParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","TargetBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Name":"Main branch release approval","Subject":"promotion-set.apply","RequiredResponder":"user:bob","NotificationUrl":"https://approvals.example.net/grace","NotificationUrlSafety":"PublicHttps","AcknowledgeUnsafeLocalDevelopment":false,"TimeoutSeconds":3600,"OnTimeout":"Reject"},
  } satisfies CreateApprovalPolicyRequest;

  try {
    const data = await api.createApprovalPolicy(body);
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
| **createApprovalPolicyParameters** | [CreateApprovalPolicyParameters](CreateApprovalPolicyParameters.md) |  | |

### Return type

[**InlineObject**](InlineObject.md)

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


## deleteApprovalPolicy

> InlineObject deleteApprovalPolicy(approvalPolicyParameters)

Delete an approval policy.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DeleteApprovalPolicyRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ApprovalPolicyParameters
    approvalPolicyParameters: ...,
  } satisfies DeleteApprovalPolicyRequest;

  try {
    const data = await api.deleteApprovalPolicy(body);
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
| **approvalPolicyParameters** | [ApprovalPolicyParameters](ApprovalPolicyParameters.md) |  | |

### Return type

[**InlineObject**](InlineObject.md)

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


## disableApprovalPolicy

> InlineObject disableApprovalPolicy(approvalPolicyParameters)

Disable an approval policy.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DisableApprovalPolicyRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ApprovalPolicyParameters
    approvalPolicyParameters: ...,
  } satisfies DisableApprovalPolicyRequest;

  try {
    const data = await api.disableApprovalPolicy(body);
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
| **approvalPolicyParameters** | [ApprovalPolicyParameters](ApprovalPolicyParameters.md) |  | |

### Return type

[**InlineObject**](InlineObject.md)

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


## enableApprovalPolicy

> InlineObject enableApprovalPolicy(approvalPolicyParameters)

Enable an approval policy.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { EnableApprovalPolicyRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ApprovalPolicyParameters
    approvalPolicyParameters: ...,
  } satisfies EnableApprovalPolicyRequest;

  try {
    const data = await api.enableApprovalPolicy(body);
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
| **approvalPolicyParameters** | [ApprovalPolicyParameters](ApprovalPolicyParameters.md) |  | |

### Return type

[**InlineObject**](InlineObject.md)

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


## evaluateApprovalPolicy

> InlineObject1 evaluateApprovalPolicy(evaluateApprovalPolicyParameters)

Evaluate approval policies for a subject.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { EvaluateApprovalPolicyRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // EvaluateApprovalPolicyParameters
    evaluateApprovalPolicyParameters: ...,
  } satisfies EvaluateApprovalPolicyRequest;

  try {
    const data = await api.evaluateApprovalPolicy(body);
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
| **evaluateApprovalPolicyParameters** | [EvaluateApprovalPolicyParameters](EvaluateApprovalPolicyParameters.md) |  | |

### Return type

[**InlineObject1**](InlineObject1.md)

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


## listApprovalPolicies

> InlineObject1 listApprovalPolicies(listApprovalPoliciesParameters)

List approval policies.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListApprovalPoliciesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ListApprovalPoliciesParameters
    listApprovalPoliciesParameters: ...,
  } satisfies ListApprovalPoliciesRequest;

  try {
    const data = await api.listApprovalPolicies(body);
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
| **listApprovalPoliciesParameters** | [ListApprovalPoliciesParameters](ListApprovalPoliciesParameters.md) |  | |

### Return type

[**InlineObject1**](InlineObject1.md)

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


## listApprovalRequests

> InlineObject2 listApprovalRequests(listApprovalRequestsParameters)

List workflow-generated approval requests.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListApprovalRequestsRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ListApprovalRequestsParameters
    listApprovalRequestsParameters: ...,
  } satisfies ListApprovalRequestsRequest;

  try {
    const data = await api.listApprovalRequests(body);
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
| **listApprovalRequestsParameters** | [ListApprovalRequestsParameters](ListApprovalRequestsParameters.md) |  | |

### Return type

[**InlineObject2**](InlineObject2.md)

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


## rejectApprovalRequest

> InlineObject3 rejectApprovalRequest(approveApprovalRequestParameters)

Reject a workflow-generated approval request.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RejectApprovalRequestRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ApproveApprovalRequestParameters
    approveApprovalRequestParameters: ...,
  } satisfies RejectApprovalRequestRequest;

  try {
    const data = await api.rejectApprovalRequest(body);
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
| **approveApprovalRequestParameters** | [ApproveApprovalRequestParameters](ApproveApprovalRequestParameters.md) |  | |

### Return type

[**InlineObject3**](InlineObject3.md)

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


## showApprovalPolicy

> InlineObject showApprovalPolicy(approvalPolicyParameters)

Show an approval policy.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ShowApprovalPolicyRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ApprovalPolicyParameters
    approvalPolicyParameters: ...,
  } satisfies ShowApprovalPolicyRequest;

  try {
    const data = await api.showApprovalPolicy(body);
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
| **approvalPolicyParameters** | [ApprovalPolicyParameters](ApprovalPolicyParameters.md) |  | |

### Return type

[**InlineObject**](InlineObject.md)

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


## showApprovalRequest

> InlineObject3 showApprovalRequest(approvalRequestParameters)

Show a workflow-generated approval request.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ShowApprovalRequestRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ApprovalRequestParameters
    approvalRequestParameters: ...,
  } satisfies ShowApprovalRequestRequest;

  try {
    const data = await api.showApprovalRequest(body);
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
| **approvalRequestParameters** | [ApprovalRequestParameters](ApprovalRequestParameters.md) |  | |

### Return type

[**InlineObject3**](InlineObject3.md)

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


## updateApprovalPolicy

> InlineObject updateApprovalPolicy(createApprovalPolicyParameters)

Update an approval policy.

### Example

```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { UpdateApprovalPolicyRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // CreateApprovalPolicyParameters
    createApprovalPolicyParameters: ...,
  } satisfies UpdateApprovalPolicyRequest;

  try {
    const data = await api.updateApprovalPolicy(body);
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
| **createApprovalPolicyParameters** | [CreateApprovalPolicyParameters](CreateApprovalPolicyParameters.md) |  | |

### Return type

[**InlineObject**](InlineObject.md)

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

