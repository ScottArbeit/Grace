# \DefaultApi

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



## claim_reuse_ranges

> models::UploadSessionDecisionReturnValue claim_reuse_ranges(claim_reuse_ranges_parameters)
Claims reusable ContentBlock ranges.

Claims server-issued candidate ranges after authoritative ContentBlockMetadata validation.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**claim_reuse_ranges_parameters** | [**ClaimReuseRangesParameters**](ClaimReuseRangesParameters.md) |  | [required] |

### Return type

[**models::UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## confirm_content_block_upload

> models::UploadSessionDecisionReturnValue confirm_content_block_upload(confirm_content_block_upload_parameters)
Confirms a ContentBlock upload.

Confirms object-storage placement for a ContentBlock that was uploaded for this manifest session.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**confirm_content_block_upload_parameters** | [**ConfirmContentBlockUploadParameters**](ConfirmContentBlockUploadParameters.md) |  | [required] |

### Return type

[**models::UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## discover_content_blocks

> models::DiscoverContentBlocksReturnValue discover_content_blocks(discover_content_blocks_parameters)
Discovers reusable ContentBlock candidates.

Returns bounded, non-authoritative candidate windows for key chunks; empty results do not prove absence.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**discover_content_blocks_parameters** | [**DiscoverContentBlocksParameters**](DiscoverContentBlocksParameters.md) |  | [required] |

### Return type

[**models::DiscoverContentBlocksReturnValue**](DiscoverContentBlocksReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## finalize_manifest_upload

> models::UploadSessionDecisionReturnValue finalize_manifest_upload(finalize_manifest_upload_parameters)
Finalizes a manifest upload.

Finalizes the manifest after validating reconstruction, range claims, block presence, file hash, and size.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**finalize_manifest_upload_parameters** | [**FinalizeManifestUploadParameters**](FinalizeManifestUploadParameters.md) |  | [required] |

### Return type

[**models::UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_content_block_download_uri

> String get_content_block_download_uri(get_content_block_download_uri_parameters)
Gets a download URI for a ContentBlock payload.

Gets an object-storage download URI for one ContentBlock in an authorized manifest-backed reconstruction path. Callers provide the server-accepted StoragePoolId, ManifestAddress, ContentBlockAddress, and AuthorizedScope; the request does not repost the full FileManifest for each block.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_content_block_download_uri_parameters** | [**GetContentBlockDownloadUriParameters**](GetContentBlockDownloadUriParameters.md) |  | [required] |

### Return type

**String**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: text/plain, application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_content_block_upload_uri

> String get_content_block_upload_uri(get_content_block_upload_uri_parameters)
Gets an upload URI for a ContentBlock payload.

Gets an object-storage upload URI without probing whether the ContentBlock already exists.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_content_block_upload_uri_parameters** | [**GetContentBlockUploadUriParameters**](GetContentBlockUploadUriParameters.md) |  | [required] |

### Return type

**String**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: text/plain, application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_download_uri

> String get_download_uri(get_download_uri_parameters)
Gets a download URI for whole-file content.

Gets an object-specific read-only SAS URI for whole-file bytes only after the server proves the requested FileVersion is reachable from an observable reference, path, and hash proof. Hidden private references and missing references return the same not-found shape. 

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_download_uri_parameters** | [**GetDownloadUriParameters**](GetDownloadUriParameters.md) |  | [required] |

### Return type

**String**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: text/plain, application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_open_api

> get_open_api()
Get the OpenAPI specification for the Grace Server API

This endpoint returns the OpenAPI specification for the Grace Server API. The specification is generated from the OpenAPI specification files in the src/OpenAPI folder.

### Parameters

This endpoint does not need any parameter.

### Return type

 (empty response body)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: Not defined
- **Accept**: Not defined

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_upload_metadata_for_files

> models::UploadMetadataArrayReturnValue get_upload_metadata_for_files(get_upload_metadata_for_files_parameters)
Gets upload metadata for files.

Gets upload URLs and content-reference metadata for whole-file compatibility uploads.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_upload_metadata_for_files_parameters** | [**GetUploadMetadataForFilesParameters**](GetUploadMetadataForFilesParameters.md) |  | [required] |

### Return type

[**models::UploadMetadataArrayReturnValue**](UploadMetadataArrayReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_upload_uri

> std::collections::HashMap<String, String> get_upload_uri(get_upload_uri_parameters)
Gets upload URIs for whole-file content.

Compatibility endpoint for uploading repository-scoped WholeFileContent.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_upload_uri_parameters** | [**GetUploadUriParameters**](GetUploadUriParameters.md) |  | [required] |

### Return type

**std::collections::HashMap<String, String>**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## issue_dedupe_discovery

> models::UploadSessionDecisionReturnValue issue_dedupe_discovery(issue_dedupe_discovery_parameters)
Issues upload-session dedupe discovery hints.

Records server-issued discovery hints for a started upload session before reuse claims.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**issue_dedupe_discovery_parameters** | [**IssueDedupeDiscoveryParameters**](IssueDedupeDiscoveryParameters.md) |  | [required] |

### Return type

[**models::UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## register_content_block_upload

> models::UploadSessionDecisionReturnValue register_content_block_upload(register_content_block_upload_parameters)
Registers a ContentBlock upload intent.

Records the expected logical range and payload size before uploading a new ContentBlock.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**register_content_block_upload_parameters** | [**RegisterContentBlockUploadParameters**](RegisterContentBlockUploadParameters.md) |  | [required] |

### Return type

[**models::UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## start_manifest_upload_session

> models::UploadSessionDecisionReturnValue start_manifest_upload_session(start_manifest_upload_session_parameters)
Starts a manifest upload session.

Starts the server-authoritative workflow for one manifest-backed file.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**start_manifest_upload_session_parameters** | [**StartManifestUploadSessionParameters**](StartManifestUploadSessionParameters.md) |  | [required] |

### Return type

[**models::UploadSessionDecisionReturnValue**](UploadSessionDecisionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

