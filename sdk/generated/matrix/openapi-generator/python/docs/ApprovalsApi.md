# grace_generated_openapi_probe.ApprovalsApi

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


# **approval_request_history**
> InlineObject2 approval_request_history(approval_request_parameters)

Show approval request history.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.approval_request_parameters import ApprovalRequestParameters
from grace_generated_openapi_probe.models.inline_object2 import InlineObject2
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    approval_request_parameters = grace_generated_openapi_probe.ApprovalRequestParameters() # ApprovalRequestParameters | 

    try:
        # Show approval request history.
        api_response = api_instance.approval_request_history(approval_request_parameters)
        print("The response of ApprovalsApi->approval_request_history:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->approval_request_history: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **approval_request_parameters** | [**ApprovalRequestParameters**](ApprovalRequestParameters.md)|  | 

### Return type

[**InlineObject2**](InlineObject2.md)

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

# **approve_approval_request**
> InlineObject3 approve_approval_request(approve_approval_request_parameters)

Approve a workflow-generated approval request.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.approve_approval_request_parameters import ApproveApprovalRequestParameters
from grace_generated_openapi_probe.models.inline_object3 import InlineObject3
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    approve_approval_request_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:bob","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","TargetBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","ApprovalRequestId":"5f9ed4df-d6df-4a65-a5d0-2830d62512c1","Reason":"Release criteria satisfied.","ClientDecisionId":"bob-approval-20260604-001"} # ApproveApprovalRequestParameters | 

    try:
        # Approve a workflow-generated approval request.
        api_response = api_instance.approve_approval_request(approve_approval_request_parameters)
        print("The response of ApprovalsApi->approve_approval_request:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->approve_approval_request: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **approve_approval_request_parameters** | [**ApproveApprovalRequestParameters**](ApproveApprovalRequestParameters.md)|  | 

### Return type

[**InlineObject3**](InlineObject3.md)

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

# **create_approval_policy**
> InlineObject create_approval_policy(create_approval_policy_parameters)

Create an approval policy.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.create_approval_policy_parameters import CreateApprovalPolicyParameters
from grace_generated_openapi_probe.models.inline_object import InlineObject
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    create_approval_policy_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","TargetBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Name":"Main branch release approval","Subject":"promotion-set.apply","RequiredResponder":"user:bob","NotificationUrl":"https://approvals.example.net/grace","NotificationUrlSafety":"PublicHttps","AcknowledgeUnsafeLocalDevelopment":false,"TimeoutSeconds":3600,"OnTimeout":"Reject"} # CreateApprovalPolicyParameters | 

    try:
        # Create an approval policy.
        api_response = api_instance.create_approval_policy(create_approval_policy_parameters)
        print("The response of ApprovalsApi->create_approval_policy:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->create_approval_policy: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_approval_policy_parameters** | [**CreateApprovalPolicyParameters**](CreateApprovalPolicyParameters.md)|  | 

### Return type

[**InlineObject**](InlineObject.md)

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

# **delete_approval_policy**
> InlineObject delete_approval_policy(approval_policy_parameters)

Delete an approval policy.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.approval_policy_parameters import ApprovalPolicyParameters
from grace_generated_openapi_probe.models.inline_object import InlineObject
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    approval_policy_parameters = grace_generated_openapi_probe.ApprovalPolicyParameters() # ApprovalPolicyParameters | 

    try:
        # Delete an approval policy.
        api_response = api_instance.delete_approval_policy(approval_policy_parameters)
        print("The response of ApprovalsApi->delete_approval_policy:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->delete_approval_policy: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **approval_policy_parameters** | [**ApprovalPolicyParameters**](ApprovalPolicyParameters.md)|  | 

### Return type

[**InlineObject**](InlineObject.md)

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

# **disable_approval_policy**
> InlineObject disable_approval_policy(approval_policy_parameters)

Disable an approval policy.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.approval_policy_parameters import ApprovalPolicyParameters
from grace_generated_openapi_probe.models.inline_object import InlineObject
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    approval_policy_parameters = grace_generated_openapi_probe.ApprovalPolicyParameters() # ApprovalPolicyParameters | 

    try:
        # Disable an approval policy.
        api_response = api_instance.disable_approval_policy(approval_policy_parameters)
        print("The response of ApprovalsApi->disable_approval_policy:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->disable_approval_policy: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **approval_policy_parameters** | [**ApprovalPolicyParameters**](ApprovalPolicyParameters.md)|  | 

### Return type

[**InlineObject**](InlineObject.md)

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

# **enable_approval_policy**
> InlineObject enable_approval_policy(approval_policy_parameters)

Enable an approval policy.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.approval_policy_parameters import ApprovalPolicyParameters
from grace_generated_openapi_probe.models.inline_object import InlineObject
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    approval_policy_parameters = grace_generated_openapi_probe.ApprovalPolicyParameters() # ApprovalPolicyParameters | 

    try:
        # Enable an approval policy.
        api_response = api_instance.enable_approval_policy(approval_policy_parameters)
        print("The response of ApprovalsApi->enable_approval_policy:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->enable_approval_policy: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **approval_policy_parameters** | [**ApprovalPolicyParameters**](ApprovalPolicyParameters.md)|  | 

### Return type

[**InlineObject**](InlineObject.md)

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

# **evaluate_approval_policy**
> InlineObject1 evaluate_approval_policy(evaluate_approval_policy_parameters)

Evaluate approval policies for a subject.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.evaluate_approval_policy_parameters import EvaluateApprovalPolicyParameters
from grace_generated_openapi_probe.models.inline_object1 import InlineObject1
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    evaluate_approval_policy_parameters = grace_generated_openapi_probe.EvaluateApprovalPolicyParameters() # EvaluateApprovalPolicyParameters | 

    try:
        # Evaluate approval policies for a subject.
        api_response = api_instance.evaluate_approval_policy(evaluate_approval_policy_parameters)
        print("The response of ApprovalsApi->evaluate_approval_policy:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->evaluate_approval_policy: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **evaluate_approval_policy_parameters** | [**EvaluateApprovalPolicyParameters**](EvaluateApprovalPolicyParameters.md)|  | 

### Return type

[**InlineObject1**](InlineObject1.md)

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

# **list_approval_policies**
> InlineObject1 list_approval_policies(list_approval_policies_parameters)

List approval policies.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object1 import InlineObject1
from grace_generated_openapi_probe.models.list_approval_policies_parameters import ListApprovalPoliciesParameters
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    list_approval_policies_parameters = grace_generated_openapi_probe.ListApprovalPoliciesParameters() # ListApprovalPoliciesParameters | 

    try:
        # List approval policies.
        api_response = api_instance.list_approval_policies(list_approval_policies_parameters)
        print("The response of ApprovalsApi->list_approval_policies:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->list_approval_policies: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **list_approval_policies_parameters** | [**ListApprovalPoliciesParameters**](ListApprovalPoliciesParameters.md)|  | 

### Return type

[**InlineObject1**](InlineObject1.md)

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

# **list_approval_requests**
> InlineObject2 list_approval_requests(list_approval_requests_parameters)

List workflow-generated approval requests.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object2 import InlineObject2
from grace_generated_openapi_probe.models.list_approval_requests_parameters import ListApprovalRequestsParameters
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    list_approval_requests_parameters = grace_generated_openapi_probe.ListApprovalRequestsParameters() # ListApprovalRequestsParameters | 

    try:
        # List workflow-generated approval requests.
        api_response = api_instance.list_approval_requests(list_approval_requests_parameters)
        print("The response of ApprovalsApi->list_approval_requests:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->list_approval_requests: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **list_approval_requests_parameters** | [**ListApprovalRequestsParameters**](ListApprovalRequestsParameters.md)|  | 

### Return type

[**InlineObject2**](InlineObject2.md)

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

# **reject_approval_request**
> InlineObject3 reject_approval_request(approve_approval_request_parameters)

Reject a workflow-generated approval request.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.approve_approval_request_parameters import ApproveApprovalRequestParameters
from grace_generated_openapi_probe.models.inline_object3 import InlineObject3
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    approve_approval_request_parameters = grace_generated_openapi_probe.ApproveApprovalRequestParameters() # ApproveApprovalRequestParameters | 

    try:
        # Reject a workflow-generated approval request.
        api_response = api_instance.reject_approval_request(approve_approval_request_parameters)
        print("The response of ApprovalsApi->reject_approval_request:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->reject_approval_request: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **approve_approval_request_parameters** | [**ApproveApprovalRequestParameters**](ApproveApprovalRequestParameters.md)|  | 

### Return type

[**InlineObject3**](InlineObject3.md)

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

# **show_approval_policy**
> InlineObject show_approval_policy(approval_policy_parameters)

Show an approval policy.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.approval_policy_parameters import ApprovalPolicyParameters
from grace_generated_openapi_probe.models.inline_object import InlineObject
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    approval_policy_parameters = grace_generated_openapi_probe.ApprovalPolicyParameters() # ApprovalPolicyParameters | 

    try:
        # Show an approval policy.
        api_response = api_instance.show_approval_policy(approval_policy_parameters)
        print("The response of ApprovalsApi->show_approval_policy:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->show_approval_policy: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **approval_policy_parameters** | [**ApprovalPolicyParameters**](ApprovalPolicyParameters.md)|  | 

### Return type

[**InlineObject**](InlineObject.md)

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

# **show_approval_request**
> InlineObject3 show_approval_request(approval_request_parameters)

Show a workflow-generated approval request.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.approval_request_parameters import ApprovalRequestParameters
from grace_generated_openapi_probe.models.inline_object3 import InlineObject3
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    approval_request_parameters = grace_generated_openapi_probe.ApprovalRequestParameters() # ApprovalRequestParameters | 

    try:
        # Show a workflow-generated approval request.
        api_response = api_instance.show_approval_request(approval_request_parameters)
        print("The response of ApprovalsApi->show_approval_request:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->show_approval_request: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **approval_request_parameters** | [**ApprovalRequestParameters**](ApprovalRequestParameters.md)|  | 

### Return type

[**InlineObject3**](InlineObject3.md)

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

# **update_approval_policy**
> InlineObject update_approval_policy(create_approval_policy_parameters)

Update an approval policy.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.create_approval_policy_parameters import CreateApprovalPolicyParameters
from grace_generated_openapi_probe.models.inline_object import InlineObject
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
    api_instance = grace_generated_openapi_probe.ApprovalsApi(api_client)
    create_approval_policy_parameters = grace_generated_openapi_probe.CreateApprovalPolicyParameters() # CreateApprovalPolicyParameters | 

    try:
        # Update an approval policy.
        api_response = api_instance.update_approval_policy(create_approval_policy_parameters)
        print("The response of ApprovalsApi->update_approval_policy:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling ApprovalsApi->update_approval_policy: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_approval_policy_parameters** | [**CreateApprovalPolicyParameters**](CreateApprovalPolicyParameters.md)|  | 

### Return type

[**InlineObject**](InlineObject.md)

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

