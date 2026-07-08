# grace_generated_openapi_probe.DefaultApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**claim_reuse_ranges**](DefaultApi.md#claim_reuse_ranges) | **POST** /storage/claimReuseRanges | Claims reusable ContentBlock ranges.
[**confirm_content_block_upload**](DefaultApi.md#confirm_content_block_upload) | **POST** /storage/confirmContentBlockUpload | Confirms a ContentBlock upload.
[**discover_content_blocks**](DefaultApi.md#discover_content_blocks) | **POST** /storage/discoverContentBlocks | Discovers reusable ContentBlock candidates.
[**finalize_manifest_upload**](DefaultApi.md#finalize_manifest_upload) | **POST** /storage/finalizeManifestUpload | Finalizes a manifest upload.
[**get_content_block_download_uri**](DefaultApi.md#get_content_block_download_uri) | **POST** /storage/getContentBlockDownloadUri | Gets a download URI for a ContentBlock payload.
[**get_content_block_upload_uri**](DefaultApi.md#get_content_block_upload_uri) | **POST** /storage/getContentBlockUploadUri | Gets an upload URI for a ContentBlock payload.
[**get_download_uri**](DefaultApi.md#get_download_uri) | **POST** /storage/getDownloadUri | Gets a download URI for whole-file content.
[**get_open_api**](DefaultApi.md#get_open_api) | **GET** /openApi | Get the OpenAPI specification for the Grace Server API
[**get_upload_metadata_for_files**](DefaultApi.md#get_upload_metadata_for_files) | **POST** /storage/getUploadMetadataForFiles | Gets upload metadata for files.
[**get_upload_uri**](DefaultApi.md#get_upload_uri) | **POST** /storage/getUploadUri | Gets upload URIs for whole-file content.
[**issue_dedupe_discovery**](DefaultApi.md#issue_dedupe_discovery) | **POST** /storage/issueDedupeDiscovery | Issues upload-session dedupe discovery hints.
[**register_content_block_upload**](DefaultApi.md#register_content_block_upload) | **POST** /storage/registerContentBlockUpload | Registers a ContentBlock upload intent.
[**start_manifest_upload_session**](DefaultApi.md#start_manifest_upload_session) | **POST** /storage/startManifestUploadSession | Starts a manifest upload session.


# **claim_reuse_ranges**
> UploadSessionDecisionReturnValue claim_reuse_ranges(claim_reuse_ranges_parameters)

Claims reusable ContentBlock ranges.

Claims server-issued candidate ranges after authoritative ContentBlockMetadata validation.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.claim_reuse_ranges_parameters import ClaimReuseRangesParameters
from grace_generated_openapi_probe.models.upload_session_decision_return_value import UploadSessionDecisionReturnValue
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    claim_reuse_ranges_parameters = grace_generated_openapi_probe.ClaimReuseRangesParameters() # ClaimReuseRangesParameters | 

    try:
        # Claims reusable ContentBlock ranges.
        api_response = api_instance.claim_reuse_ranges(claim_reuse_ranges_parameters)
        print("The response of DefaultApi->claim_reuse_ranges:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->claim_reuse_ranges: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **claim_reuse_ranges_parameters** | [**ClaimReuseRangesParameters**](ClaimReuseRangesParameters.md)|  | 

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

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

# **confirm_content_block_upload**
> UploadSessionDecisionReturnValue confirm_content_block_upload(confirm_content_block_upload_parameters)

Confirms a ContentBlock upload.

Confirms object-storage placement for a ContentBlock that was uploaded for this manifest session.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.confirm_content_block_upload_parameters import ConfirmContentBlockUploadParameters
from grace_generated_openapi_probe.models.upload_session_decision_return_value import UploadSessionDecisionReturnValue
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    confirm_content_block_upload_parameters = grace_generated_openapi_probe.ConfirmContentBlockUploadParameters() # ConfirmContentBlockUploadParameters | 

    try:
        # Confirms a ContentBlock upload.
        api_response = api_instance.confirm_content_block_upload(confirm_content_block_upload_parameters)
        print("The response of DefaultApi->confirm_content_block_upload:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->confirm_content_block_upload: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **confirm_content_block_upload_parameters** | [**ConfirmContentBlockUploadParameters**](ConfirmContentBlockUploadParameters.md)|  | 

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

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

# **discover_content_blocks**
> DiscoverContentBlocksReturnValue discover_content_blocks(discover_content_blocks_parameters)

Discovers reusable ContentBlock candidates.

Returns bounded, non-authoritative candidate windows for key chunks; empty results do not prove absence.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.discover_content_blocks_parameters import DiscoverContentBlocksParameters
from grace_generated_openapi_probe.models.discover_content_blocks_return_value import DiscoverContentBlocksReturnValue
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    discover_content_blocks_parameters = grace_generated_openapi_probe.DiscoverContentBlocksParameters() # DiscoverContentBlocksParameters | 

    try:
        # Discovers reusable ContentBlock candidates.
        api_response = api_instance.discover_content_blocks(discover_content_blocks_parameters)
        print("The response of DefaultApi->discover_content_blocks:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->discover_content_blocks: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **discover_content_blocks_parameters** | [**DiscoverContentBlocksParameters**](DiscoverContentBlocksParameters.md)|  | 

### Return type

[**DiscoverContentBlocksReturnValue**](DiscoverContentBlocksReturnValue.md)

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

# **finalize_manifest_upload**
> UploadSessionDecisionReturnValue finalize_manifest_upload(finalize_manifest_upload_parameters)

Finalizes a manifest upload.

Finalizes the manifest after validating reconstruction, range claims, block presence, file hash, and size.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.finalize_manifest_upload_parameters import FinalizeManifestUploadParameters
from grace_generated_openapi_probe.models.upload_session_decision_return_value import UploadSessionDecisionReturnValue
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    finalize_manifest_upload_parameters = grace_generated_openapi_probe.FinalizeManifestUploadParameters() # FinalizeManifestUploadParameters | 

    try:
        # Finalizes a manifest upload.
        api_response = api_instance.finalize_manifest_upload(finalize_manifest_upload_parameters)
        print("The response of DefaultApi->finalize_manifest_upload:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->finalize_manifest_upload: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **finalize_manifest_upload_parameters** | [**FinalizeManifestUploadParameters**](FinalizeManifestUploadParameters.md)|  | 

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

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

# **get_content_block_download_uri**
> str get_content_block_download_uri(get_content_block_download_uri_parameters)

Gets a download URI for a ContentBlock payload.

Gets an object-storage download URI for one ContentBlock in an authorized manifest-backed reconstruction path. Callers provide the server-accepted StoragePoolId, ManifestAddress, ContentBlockAddress, and AuthorizedScope; the request does not repost the full FileManifest for each block.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_content_block_download_uri_parameters import GetContentBlockDownloadUriParameters
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    get_content_block_download_uri_parameters = grace_generated_openapi_probe.GetContentBlockDownloadUriParameters() # GetContentBlockDownloadUriParameters | 

    try:
        # Gets a download URI for a ContentBlock payload.
        api_response = api_instance.get_content_block_download_uri(get_content_block_download_uri_parameters)
        print("The response of DefaultApi->get_content_block_download_uri:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->get_content_block_download_uri: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_content_block_download_uri_parameters** | [**GetContentBlockDownloadUriParameters**](GetContentBlockDownloadUriParameters.md)|  | 

### Return type

**str**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: text/plain, application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_content_block_upload_uri**
> str get_content_block_upload_uri(get_content_block_upload_uri_parameters)

Gets an upload URI for a ContentBlock payload.

Gets an object-storage upload URI without probing whether the ContentBlock already exists.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_content_block_upload_uri_parameters import GetContentBlockUploadUriParameters
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    get_content_block_upload_uri_parameters = grace_generated_openapi_probe.GetContentBlockUploadUriParameters() # GetContentBlockUploadUriParameters | 

    try:
        # Gets an upload URI for a ContentBlock payload.
        api_response = api_instance.get_content_block_upload_uri(get_content_block_upload_uri_parameters)
        print("The response of DefaultApi->get_content_block_upload_uri:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->get_content_block_upload_uri: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_content_block_upload_uri_parameters** | [**GetContentBlockUploadUriParameters**](GetContentBlockUploadUriParameters.md)|  | 

### Return type

**str**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: text/plain, application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_download_uri**
> str get_download_uri(get_download_uri_parameters)

Gets a download URI for whole-file content.

Gets an object-specific read-only SAS URI for whole-file bytes only after the server proves the requested FileVersion is reachable from an observable reference, path, and hash proof. Hidden private references and missing references return the same not-found shape.


### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_download_uri_parameters import GetDownloadUriParameters
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    get_download_uri_parameters = grace_generated_openapi_probe.GetDownloadUriParameters() # GetDownloadUriParameters | 

    try:
        # Gets a download URI for whole-file content.
        api_response = api_instance.get_download_uri(get_download_uri_parameters)
        print("The response of DefaultApi->get_download_uri:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->get_download_uri: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_download_uri_parameters** | [**GetDownloadUriParameters**](GetDownloadUriParameters.md)|  | 

### Return type

**str**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: text/plain, application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**404** | Not Found |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_open_api**
> get_open_api()

Get the OpenAPI specification for the Grace Server API

This endpoint returns the OpenAPI specification for the Grace Server API. The specification is generated from the OpenAPI specification files in the src/OpenAPI folder.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)

    try:
        # Get the OpenAPI specification for the Grace Server API
        api_instance.get_open_api()
    except Exception as e:
        print("Exception when calling DefaultApi->get_open_api: %s\n" % e)
```



### Parameters

This endpoint does not need any parameter.

### Return type

void (empty response body)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: Not defined
 - **Accept**: Not defined

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **get_upload_metadata_for_files**
> UploadMetadataArrayReturnValue get_upload_metadata_for_files(get_upload_metadata_for_files_parameters)

Gets upload metadata for files.

Gets upload URLs and content-reference metadata for whole-file compatibility uploads.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_upload_metadata_for_files_parameters import GetUploadMetadataForFilesParameters
from grace_generated_openapi_probe.models.upload_metadata_array_return_value import UploadMetadataArrayReturnValue
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    get_upload_metadata_for_files_parameters = grace_generated_openapi_probe.GetUploadMetadataForFilesParameters() # GetUploadMetadataForFilesParameters | 

    try:
        # Gets upload metadata for files.
        api_response = api_instance.get_upload_metadata_for_files(get_upload_metadata_for_files_parameters)
        print("The response of DefaultApi->get_upload_metadata_for_files:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->get_upload_metadata_for_files: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_upload_metadata_for_files_parameters** | [**GetUploadMetadataForFilesParameters**](GetUploadMetadataForFilesParameters.md)|  | 

### Return type

[**UploadMetadataArrayReturnValue**](UploadMetadataArrayReturnValue.md)

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

# **get_upload_uri**
> Dict[str, str] get_upload_uri(get_upload_uri_parameters)

Gets upload URIs for whole-file content.

Compatibility endpoint for uploading repository-scoped WholeFileContent.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.get_upload_uri_parameters import GetUploadUriParameters
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    get_upload_uri_parameters = grace_generated_openapi_probe.GetUploadUriParameters() # GetUploadUriParameters | 

    try:
        # Gets upload URIs for whole-file content.
        api_response = api_instance.get_upload_uri(get_upload_uri_parameters)
        print("The response of DefaultApi->get_upload_uri:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->get_upload_uri: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **get_upload_uri_parameters** | [**GetUploadUriParameters**](GetUploadUriParameters.md)|  | 

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

# **issue_dedupe_discovery**
> UploadSessionDecisionReturnValue issue_dedupe_discovery(issue_dedupe_discovery_parameters)

Issues upload-session dedupe discovery hints.

Records server-issued discovery hints for a started upload session before reuse claims.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.issue_dedupe_discovery_parameters import IssueDedupeDiscoveryParameters
from grace_generated_openapi_probe.models.upload_session_decision_return_value import UploadSessionDecisionReturnValue
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    issue_dedupe_discovery_parameters = grace_generated_openapi_probe.IssueDedupeDiscoveryParameters() # IssueDedupeDiscoveryParameters | 

    try:
        # Issues upload-session dedupe discovery hints.
        api_response = api_instance.issue_dedupe_discovery(issue_dedupe_discovery_parameters)
        print("The response of DefaultApi->issue_dedupe_discovery:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->issue_dedupe_discovery: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **issue_dedupe_discovery_parameters** | [**IssueDedupeDiscoveryParameters**](IssueDedupeDiscoveryParameters.md)|  | 

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

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

# **register_content_block_upload**
> UploadSessionDecisionReturnValue register_content_block_upload(register_content_block_upload_parameters)

Registers a ContentBlock upload intent.

Records the expected logical range and payload size before uploading a new ContentBlock.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.register_content_block_upload_parameters import RegisterContentBlockUploadParameters
from grace_generated_openapi_probe.models.upload_session_decision_return_value import UploadSessionDecisionReturnValue
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    register_content_block_upload_parameters = grace_generated_openapi_probe.RegisterContentBlockUploadParameters() # RegisterContentBlockUploadParameters | 

    try:
        # Registers a ContentBlock upload intent.
        api_response = api_instance.register_content_block_upload(register_content_block_upload_parameters)
        print("The response of DefaultApi->register_content_block_upload:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->register_content_block_upload: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **register_content_block_upload_parameters** | [**RegisterContentBlockUploadParameters**](RegisterContentBlockUploadParameters.md)|  | 

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

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

# **start_manifest_upload_session**
> UploadSessionDecisionReturnValue start_manifest_upload_session(start_manifest_upload_session_parameters)

Starts a manifest upload session.

Starts the server-authoritative workflow for one manifest-backed file.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.start_manifest_upload_session_parameters import StartManifestUploadSessionParameters
from grace_generated_openapi_probe.models.upload_session_decision_return_value import UploadSessionDecisionReturnValue
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
    api_instance = grace_generated_openapi_probe.DefaultApi(api_client)
    start_manifest_upload_session_parameters = grace_generated_openapi_probe.StartManifestUploadSessionParameters() # StartManifestUploadSessionParameters | 

    try:
        # Starts a manifest upload session.
        api_response = api_instance.start_manifest_upload_session(start_manifest_upload_session_parameters)
        print("The response of DefaultApi->start_manifest_upload_session:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling DefaultApi->start_manifest_upload_session: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **start_manifest_upload_session_parameters** | [**StartManifestUploadSessionParameters**](StartManifestUploadSessionParameters.md)|  | 

### Return type

[**UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

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

