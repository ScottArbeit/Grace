# \ApprovalsApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**approval_request_history**](ApprovalsApi.md#approval_request_history) | **POST** /approval/request/history | Show approval request history.
[**approve_approval_request**](ApprovalsApi.md#approve_approval_request) | **POST** /approval/request/approve | Approve a workflow-generated approval request.
[**create_approval_policy**](ApprovalsApi.md#create_approval_policy) | **POST** /approval/policy/create | Create an approval policy.
[**delete_approval_policy**](ApprovalsApi.md#delete_approval_policy) | **POST** /approval/policy/delete | Delete an approval policy.
[**disable_approval_policy**](ApprovalsApi.md#disable_approval_policy) | **POST** /approval/policy/disable | Disable an approval policy.
[**enable_approval_policy**](ApprovalsApi.md#enable_approval_policy) | **POST** /approval/policy/enable | Enable an approval policy.
[**evaluate_approval_policy**](ApprovalsApi.md#evaluate_approval_policy) | **POST** /approval/policy/evaluate | Evaluate approval policies for a subject.
[**list_approval_policies**](ApprovalsApi.md#list_approval_policies) | **POST** /approval/policy/list | List approval policies.
[**list_approval_requests**](ApprovalsApi.md#list_approval_requests) | **POST** /approval/request/list | List workflow-generated approval requests.
[**reject_approval_request**](ApprovalsApi.md#reject_approval_request) | **POST** /approval/request/reject | Reject a workflow-generated approval request.
[**show_approval_policy**](ApprovalsApi.md#show_approval_policy) | **POST** /approval/policy/show | Show an approval policy.
[**show_approval_request**](ApprovalsApi.md#show_approval_request) | **POST** /approval/request/show | Show a workflow-generated approval request.
[**update_approval_policy**](ApprovalsApi.md#update_approval_policy) | **POST** /approval/policy/update | Update an approval policy.



## approval_request_history

> models::InlineObject2 approval_request_history(approval_request_parameters)
Show approval request history.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**approval_request_parameters** | [**ApprovalRequestParameters**](ApprovalRequestParameters.md) |  | [required] |

### Return type

[**models::InlineObject2**](inline_object_2.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## approve_approval_request

> models::InlineObject3 approve_approval_request(approve_approval_request_parameters)
Approve a workflow-generated approval request.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**approve_approval_request_parameters** | [**ApproveApprovalRequestParameters**](ApproveApprovalRequestParameters.md) |  | [required] |

### Return type

[**models::InlineObject3**](inline_object_3.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## create_approval_policy

> models::InlineObject create_approval_policy(create_approval_policy_parameters)
Create an approval policy.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_approval_policy_parameters** | [**CreateApprovalPolicyParameters**](CreateApprovalPolicyParameters.md) |  | [required] |

### Return type

[**models::InlineObject**](inline_object.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## delete_approval_policy

> models::InlineObject delete_approval_policy(approval_policy_parameters)
Delete an approval policy.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**approval_policy_parameters** | [**ApprovalPolicyParameters**](ApprovalPolicyParameters.md) |  | [required] |

### Return type

[**models::InlineObject**](inline_object.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## disable_approval_policy

> models::InlineObject disable_approval_policy(approval_policy_parameters)
Disable an approval policy.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**approval_policy_parameters** | [**ApprovalPolicyParameters**](ApprovalPolicyParameters.md) |  | [required] |

### Return type

[**models::InlineObject**](inline_object.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## enable_approval_policy

> models::InlineObject enable_approval_policy(approval_policy_parameters)
Enable an approval policy.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**approval_policy_parameters** | [**ApprovalPolicyParameters**](ApprovalPolicyParameters.md) |  | [required] |

### Return type

[**models::InlineObject**](inline_object.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## evaluate_approval_policy

> models::InlineObject1 evaluate_approval_policy(evaluate_approval_policy_parameters)
Evaluate approval policies for a subject.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**evaluate_approval_policy_parameters** | [**EvaluateApprovalPolicyParameters**](EvaluateApprovalPolicyParameters.md) |  | [required] |

### Return type

[**models::InlineObject1**](inline_object_1.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_approval_policies

> models::InlineObject1 list_approval_policies(list_approval_policies_parameters)
List approval policies.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**list_approval_policies_parameters** | [**ListApprovalPoliciesParameters**](ListApprovalPoliciesParameters.md) |  | [required] |

### Return type

[**models::InlineObject1**](inline_object_1.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_approval_requests

> models::InlineObject2 list_approval_requests(list_approval_requests_parameters)
List workflow-generated approval requests.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**list_approval_requests_parameters** | [**ListApprovalRequestsParameters**](ListApprovalRequestsParameters.md) |  | [required] |

### Return type

[**models::InlineObject2**](inline_object_2.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## reject_approval_request

> models::InlineObject3 reject_approval_request(approve_approval_request_parameters)
Reject a workflow-generated approval request.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**approve_approval_request_parameters** | [**ApproveApprovalRequestParameters**](ApproveApprovalRequestParameters.md) |  | [required] |

### Return type

[**models::InlineObject3**](inline_object_3.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## show_approval_policy

> models::InlineObject show_approval_policy(approval_policy_parameters)
Show an approval policy.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**approval_policy_parameters** | [**ApprovalPolicyParameters**](ApprovalPolicyParameters.md) |  | [required] |

### Return type

[**models::InlineObject**](inline_object.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## show_approval_request

> models::InlineObject3 show_approval_request(approval_request_parameters)
Show a workflow-generated approval request.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**approval_request_parameters** | [**ApprovalRequestParameters**](ApprovalRequestParameters.md) |  | [required] |

### Return type

[**models::InlineObject3**](inline_object_3.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## update_approval_policy

> models::InlineObject update_approval_policy(create_approval_policy_parameters)
Update an approval policy.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_approval_policy_parameters** | [**CreateApprovalPolicyParameters**](CreateApprovalPolicyParameters.md) |  | [required] |

### Return type

[**models::InlineObject**](inline_object.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

