# grace_generated_openapi_probe.BranchesApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**annotate_branch**](BranchesApi.md#annotate_branch) | **POST** /branch/annotate | Annotate a branch reference.
[**checkpoint_branch**](BranchesApi.md#checkpoint_branch) | **POST** /branch/checkpoint | Checkpoint the current branch content.
[**commit_branch**](BranchesApi.md#commit_branch) | **POST** /branch/commit | Commit the current branch content.
[**create_branch**](BranchesApi.md#create_branch) | **POST** /branch/create | Create a branch.
[**delete_branch**](BranchesApi.md#delete_branch) | **POST** /branch/delete | Delete a branch.
[**enable_branch_checkpoint**](BranchesApi.md#enable_branch_checkpoint) | **POST** /branch/enableCheckpoint | Enable or disable checkpoint references.
[**enable_branch_commit**](BranchesApi.md#enable_branch_commit) | **POST** /branch/enableCommit | Enable or disable commit references.
[**enable_branch_promotion**](BranchesApi.md#enable_branch_promotion) | **POST** /branch/enablePromotion | Enable or disable promotion references.
[**enable_branch_save**](BranchesApi.md#enable_branch_save) | **POST** /branch/enableSave | Enable or disable save references.
[**enable_branch_tag**](BranchesApi.md#enable_branch_tag) | **POST** /branch/enableTag | Enable or disable tag references.
[**get_branch**](BranchesApi.md#get_branch) | **POST** /branch/get | Get a branch.
[**get_branch_reference**](BranchesApi.md#get_branch_reference) | **POST** /branch/getReference | Get a branch reference.
[**get_parent_branch**](BranchesApi.md#get_parent_branch) | **POST** /branch/getParentBranch | Get the parent branch.
[**list_branch_checkpoints**](BranchesApi.md#list_branch_checkpoints) | **POST** /branch/getCheckpoints | List branch checkpoints.
[**list_branch_commits**](BranchesApi.md#list_branch_commits) | **POST** /branch/getCommits | List branch commits.
[**list_branch_promotions**](BranchesApi.md#list_branch_promotions) | **POST** /branch/getPromotions | List branch promotions.
[**list_branch_references**](BranchesApi.md#list_branch_references) | **POST** /branch/getReferences | List branch references.
[**list_branch_saves**](BranchesApi.md#list_branch_saves) | **POST** /branch/getSaves | List branch saves.
[**list_branch_tags**](BranchesApi.md#list_branch_tags) | **POST** /branch/getTags | List branch tags.
[**promote_branch**](BranchesApi.md#promote_branch) | **POST** /branch/promote | Promote the current branch content.
[**rebase_branch**](BranchesApi.md#rebase_branch) | **POST** /branch/rebase | Rebase a branch.
[**save_branch**](BranchesApi.md#save_branch) | **POST** /branch/save | Save the current branch content.
[**tag_branch**](BranchesApi.md#tag_branch) | **POST** /branch/tag | Tag the current branch content.


# **annotate_branch**
> BranchAnnotationReturnValue annotate_branch(annotate_parameters)

Annotate a branch reference.

Annotates lines from an existing server-known reference in a branch. The API reads server-stored reference content and does not save local workspace state.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.annotate_parameters import AnnotateParameters
from grace_generated_openapi_probe.models.branch_annotation_return_value import BranchAnnotationReturnValue
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    annotate_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","IncludeDeleted":false,"TargetReferenceId":"c8f9bac8-d489-46c7-917f-b36b7d9efa9a","Path":"src/App.fs","StartLine":1,"EndLine":1,"ReferenceTypes":["Commit","Save"],"MaxReferences":1000,"IncludeLineText":true} # AnnotateParameters | 

    try:
        # Annotate a branch reference.
        api_response = api_instance.annotate_branch(annotate_parameters)
        print("The response of BranchesApi->annotate_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->annotate_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **annotate_parameters** | [**AnnotateParameters**](AnnotateParameters.md)|  | 

### Return type

[**BranchAnnotationReturnValue**](BranchAnnotationReturnValue.md)

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

# **checkpoint_branch**
> BranchCommandReturnValue checkpoint_branch(create_reference_parameters)

Checkpoint the current branch content.

Creates a checkpoint reference pointing to the current root directory version in the branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.create_reference_parameters import CreateReferenceParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    create_reference_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243","Blake3Hash":"9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d","Message":"Checkpoint release candidate."} # CreateReferenceParameters | 

    try:
        # Checkpoint the current branch content.
        api_response = api_instance.checkpoint_branch(create_reference_parameters)
        print("The response of BranchesApi->checkpoint_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->checkpoint_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_reference_parameters** | [**CreateReferenceParameters**](CreateReferenceParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **commit_branch**
> BranchCommandReturnValue commit_branch(create_reference_parameters)

Commit the current branch content.

Creates a commit reference pointing to the current root directory version in the branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.create_reference_parameters import CreateReferenceParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    create_reference_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243","Blake3Hash":"9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d","Message":"Commit release candidate."} # CreateReferenceParameters | 

    try:
        # Commit the current branch content.
        api_response = api_instance.commit_branch(create_reference_parameters)
        print("The response of BranchesApi->commit_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->commit_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_reference_parameters** | [**CreateReferenceParameters**](CreateReferenceParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **create_branch**
> BranchCommandReturnValue create_branch(create_branch_parameters)

Create a branch.

Creates a branch with the specified name, based on the specified parent branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.create_branch_parameters import CreateBranchParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    create_branch_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchName":"release-2026-06","ParentBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","InitialPermissions":["Commit","Checkpoint","Save","Tag"]} # CreateBranchParameters | 

    try:
        # Create a branch.
        api_response = api_instance.create_branch(create_branch_parameters)
        print("The response of BranchesApi->create_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->create_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_branch_parameters** | [**CreateBranchParameters**](CreateBranchParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **delete_branch**
> BranchCommandReturnValue delete_branch(delete_branch_parameters)

Delete a branch.

Deletes the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.delete_branch_parameters import DeleteBranchParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    delete_branch_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true,"Force":false,"DeleteReason":"Superseded by release branch.","ReassignChildBranches":true,"NewParentBranchId":"11111111-1111-1111-1111-111111111111"} # DeleteBranchParameters | 

    try:
        # Delete a branch.
        api_response = api_instance.delete_branch(delete_branch_parameters)
        print("The response of BranchesApi->delete_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->delete_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **delete_branch_parameters** | [**DeleteBranchParameters**](DeleteBranchParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **enable_branch_checkpoint**
> BranchCommandReturnValue enable_branch_checkpoint(enable_feature_parameters)

Enable or disable checkpoint references.

Enables or disables checkpoint for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.enable_feature_parameters import EnableFeatureParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    enable_feature_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true} # EnableFeatureParameters | 

    try:
        # Enable or disable checkpoint references.
        api_response = api_instance.enable_branch_checkpoint(enable_feature_parameters)
        print("The response of BranchesApi->enable_branch_checkpoint:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->enable_branch_checkpoint: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **enable_feature_parameters** | [**EnableFeatureParameters**](EnableFeatureParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **enable_branch_commit**
> BranchCommandReturnValue enable_branch_commit(enable_feature_parameters)

Enable or disable commit references.

Enables or disables commit for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.enable_feature_parameters import EnableFeatureParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    enable_feature_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true} # EnableFeatureParameters | 

    try:
        # Enable or disable commit references.
        api_response = api_instance.enable_branch_commit(enable_feature_parameters)
        print("The response of BranchesApi->enable_branch_commit:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->enable_branch_commit: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **enable_feature_parameters** | [**EnableFeatureParameters**](EnableFeatureParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **enable_branch_promotion**
> BranchCommandReturnValue enable_branch_promotion(enable_feature_parameters)

Enable or disable promotion references.

Enables or disables promotion for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.enable_feature_parameters import EnableFeatureParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    enable_feature_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true} # EnableFeatureParameters | 

    try:
        # Enable or disable promotion references.
        api_response = api_instance.enable_branch_promotion(enable_feature_parameters)
        print("The response of BranchesApi->enable_branch_promotion:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->enable_branch_promotion: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **enable_feature_parameters** | [**EnableFeatureParameters**](EnableFeatureParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **enable_branch_save**
> BranchCommandReturnValue enable_branch_save(enable_feature_parameters)

Enable or disable save references.

Enables or disables save for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.enable_feature_parameters import EnableFeatureParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    enable_feature_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true} # EnableFeatureParameters | 

    try:
        # Enable or disable save references.
        api_response = api_instance.enable_branch_save(enable_feature_parameters)
        print("The response of BranchesApi->enable_branch_save:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->enable_branch_save: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **enable_feature_parameters** | [**EnableFeatureParameters**](EnableFeatureParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **enable_branch_tag**
> BranchCommandReturnValue enable_branch_tag(enable_feature_parameters)

Enable or disable tag references.

Enables or disables tag for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.enable_feature_parameters import EnableFeatureParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    enable_feature_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true} # EnableFeatureParameters | 

    try:
        # Enable or disable tag references.
        api_response = api_instance.enable_branch_tag(enable_feature_parameters)
        print("The response of BranchesApi->enable_branch_tag:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->enable_branch_tag: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **enable_feature_parameters** | [**EnableFeatureParameters**](EnableFeatureParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **get_branch**
> BranchReturnValue get_branch(get_branch_parameters)

Get a branch.

Gets the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_return_value import BranchReturnValue
from grace_generated_openapi_probe.models.get_branch_parameters import GetBranchParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    get_branch_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","IncludeDeleted":false} # GetBranchParameters | 

    try:
        # Get a branch.
        api_response = api_instance.get_branch(get_branch_parameters)
        print("The response of BranchesApi->get_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->get_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_branch_parameters** | [**GetBranchParameters**](GetBranchParameters.md)|  | 

### Return type

[**BranchReturnValue**](BranchReturnValue.md)

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

# **get_branch_reference**
> ReferenceReturnValue get_branch_reference(get_reference_parameters)

Get a branch reference.

Gets the specified reference from the branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_reference_parameters import GetReferenceParameters
from grace_generated_openapi_probe.models.reference_return_value import ReferenceReturnValue
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    get_reference_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","IncludeDeleted":false,"ReferenceId":"c8f9bac8-d489-46c7-917f-b36b7d9efa9a"} # GetReferenceParameters | 

    try:
        # Get a branch reference.
        api_response = api_instance.get_branch_reference(get_reference_parameters)
        print("The response of BranchesApi->get_branch_reference:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->get_branch_reference: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_reference_parameters** | [**GetReferenceParameters**](GetReferenceParameters.md)|  | 

### Return type

[**ReferenceReturnValue**](ReferenceReturnValue.md)

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

# **get_parent_branch**
> BranchReturnValue get_parent_branch(get_branch_parameters)

Get the parent branch.

Gets the parent branch of the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_return_value import BranchReturnValue
from grace_generated_openapi_probe.models.get_branch_parameters import GetBranchParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    get_branch_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","IncludeDeleted":false} # GetBranchParameters | 

    try:
        # Get the parent branch.
        api_response = api_instance.get_parent_branch(get_branch_parameters)
        print("The response of BranchesApi->get_parent_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->get_parent_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_branch_parameters** | [**GetBranchParameters**](GetBranchParameters.md)|  | 

### Return type

[**BranchReturnValue**](BranchReturnValue.md)

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

# **list_branch_checkpoints**
> ReferenceListReturnValue list_branch_checkpoints(get_references_parameters)

List branch checkpoints.

Gets the checkpoint references for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_references_parameters import GetReferencesParameters
from grace_generated_openapi_probe.models.reference_list_return_value import ReferenceListReturnValue
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    get_references_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50} # GetReferencesParameters | 

    try:
        # List branch checkpoints.
        api_response = api_instance.list_branch_checkpoints(get_references_parameters)
        print("The response of BranchesApi->list_branch_checkpoints:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->list_branch_checkpoints: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md)|  | 

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

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

# **list_branch_commits**
> ReferenceListReturnValue list_branch_commits(get_references_parameters)

List branch commits.

Gets the commit references for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_references_parameters import GetReferencesParameters
from grace_generated_openapi_probe.models.reference_list_return_value import ReferenceListReturnValue
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    get_references_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50} # GetReferencesParameters | 

    try:
        # List branch commits.
        api_response = api_instance.list_branch_commits(get_references_parameters)
        print("The response of BranchesApi->list_branch_commits:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->list_branch_commits: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md)|  | 

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

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

# **list_branch_promotions**
> ReferenceListReturnValue list_branch_promotions(get_references_parameters)

List branch promotions.

Gets the promotion references for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_references_parameters import GetReferencesParameters
from grace_generated_openapi_probe.models.reference_list_return_value import ReferenceListReturnValue
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    get_references_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50} # GetReferencesParameters | 

    try:
        # List branch promotions.
        api_response = api_instance.list_branch_promotions(get_references_parameters)
        print("The response of BranchesApi->list_branch_promotions:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->list_branch_promotions: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md)|  | 

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

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

# **list_branch_references**
> ReferenceListReturnValue list_branch_references(get_references_parameters)

List branch references.

Gets the references for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_references_parameters import GetReferencesParameters
from grace_generated_openapi_probe.models.reference_list_return_value import ReferenceListReturnValue
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    get_references_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50} # GetReferencesParameters | 

    try:
        # List branch references.
        api_response = api_instance.list_branch_references(get_references_parameters)
        print("The response of BranchesApi->list_branch_references:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->list_branch_references: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md)|  | 

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

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

# **list_branch_saves**
> ReferenceListReturnValue list_branch_saves(get_references_parameters)

List branch saves.

Gets the save references for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_references_parameters import GetReferencesParameters
from grace_generated_openapi_probe.models.reference_list_return_value import ReferenceListReturnValue
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    get_references_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50} # GetReferencesParameters | 

    try:
        # List branch saves.
        api_response = api_instance.list_branch_saves(get_references_parameters)
        print("The response of BranchesApi->list_branch_saves:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->list_branch_saves: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md)|  | 

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

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

# **list_branch_tags**
> ReferenceListReturnValue list_branch_tags(get_references_parameters)

List branch tags.

Gets the tag references for the specified branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_references_parameters import GetReferencesParameters
from grace_generated_openapi_probe.models.reference_list_return_value import ReferenceListReturnValue
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    get_references_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50} # GetReferencesParameters | 

    try:
        # List branch tags.
        api_response = api_instance.list_branch_tags(get_references_parameters)
        print("The response of BranchesApi->list_branch_tags:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->list_branch_tags: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md)|  | 

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

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

# **promote_branch**
> BranchCommandReturnValue promote_branch(create_reference_parameters)

Promote the current branch content.

Creates a promotion reference in the parent of the specified branch, based on the current directory version.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.create_reference_parameters import CreateReferenceParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    create_reference_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243","Blake3Hash":"9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d","Message":"Capture release candidate."} # CreateReferenceParameters | 

    try:
        # Promote the current branch content.
        api_response = api_instance.promote_branch(create_reference_parameters)
        print("The response of BranchesApi->promote_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->promote_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_reference_parameters** | [**CreateReferenceParameters**](CreateReferenceParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **rebase_branch**
> BranchCommandReturnValue rebase_branch(rebase_parameters)

Rebase a branch.

Rebases a branch on the specified reference from its parent branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.rebase_parameters import RebaseParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    rebase_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchName":"release-2026-06","ParentBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","InitialPermissions":["Commit","Checkpoint","Save","Tag"],"BasedOn":"c8f9bac8-d489-46c7-917f-b36b7d9efa9a"} # RebaseParameters | 

    try:
        # Rebase a branch.
        api_response = api_instance.rebase_branch(rebase_parameters)
        print("The response of BranchesApi->rebase_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->rebase_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **rebase_parameters** | [**RebaseParameters**](RebaseParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **save_branch**
> BranchCommandReturnValue save_branch(create_reference_parameters)

Save the current branch content.

Creates a save reference pointing to the current root directory version in the branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.create_reference_parameters import CreateReferenceParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    create_reference_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243","Blake3Hash":"9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d","Message":"Save release candidate."} # CreateReferenceParameters | 

    try:
        # Save the current branch content.
        api_response = api_instance.save_branch(create_reference_parameters)
        print("The response of BranchesApi->save_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->save_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_reference_parameters** | [**CreateReferenceParameters**](CreateReferenceParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

# **tag_branch**
> BranchCommandReturnValue tag_branch(create_reference_parameters)

Tag the current branch content.

Creates a tag reference pointing to the current root directory version in the branch.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.branch_command_return_value import BranchCommandReturnValue
from grace_generated_openapi_probe.models.create_reference_parameters import CreateReferenceParameters
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
    api_instance = grace_generated_openapi_probe.BranchesApi(api_client)
    create_reference_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243","Blake3Hash":"9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d","Message":"Tag release candidate."} # CreateReferenceParameters | 

    try:
        # Tag the current branch content.
        api_response = api_instance.tag_branch(create_reference_parameters)
        print("The response of BranchesApi->tag_branch:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling BranchesApi->tag_branch: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_reference_parameters** | [**CreateReferenceParameters**](CreateReferenceParameters.md)|  | 

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

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

