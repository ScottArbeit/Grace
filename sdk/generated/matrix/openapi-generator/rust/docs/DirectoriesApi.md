# \DirectoriesApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_directory_version**](DirectoriesApi.md#create_directory_version) | **POST** /directory/create | Create a new directory version.
[**get_directory_version**](DirectoriesApi.md#get_directory_version) | **POST** /directory/get | Get a directory version.
[**get_directory_version_by_blake3_hash**](DirectoriesApi.md#get_directory_version_by_blake3_hash) | **POST** /directory/getByBlake3Hash | Get a directory version by BLAKE3 hash.
[**get_directory_version_by_sha256_hash**](DirectoriesApi.md#get_directory_version_by_sha256_hash) | **POST** /directory/getBySha256Hash | Get a directory version by SHA-256 hash.
[**list_directory_versions_by_id**](DirectoriesApi.md#list_directory_versions_by_id) | **POST** /directory/getByDirectoryIds | List directory versions by id.
[**list_directory_versions_recursive**](DirectoriesApi.md#list_directory_versions_recursive) | **POST** /directory/getDirectoryVersionsRecursive | List a directory version and its children.
[**save_directory_versions**](DirectoriesApi.md#save_directory_versions) | **POST** /directory/saveDirectoryVersions | Save directory versions.



## create_directory_version

> models::DirectoryCommandReturnValue create_directory_version(create_parameters)
Create a new directory version.

Creates a directory version for the repository.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_parameters** | [**CreateParameters**](CreateParameters.md) |  | [required] |

### Return type

[**models::DirectoryCommandReturnValue**](DirectoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_directory_version

> models::DirectoryVersionReturnValue get_directory_version(get_parameters)
Get a directory version.

Gets a directory version DTO.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_parameters** | [**GetParameters**](GetParameters.md) |  | [required] |

### Return type

[**models::DirectoryVersionReturnValue**](DirectoryVersionReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_directory_version_by_blake3_hash

> models::DirectoryVersionHashLookupReturnValue get_directory_version_by_blake3_hash(get_by_blake3_hash_parameters)
Get a directory version by BLAKE3 hash.

Gets a directory version DTO by BLAKE3 hash or unique BLAKE3 prefix.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_by_blake3_hash_parameters** | [**GetByBlake3HashParameters**](GetByBlake3HashParameters.md) |  | [required] |

### Return type

[**models::DirectoryVersionHashLookupReturnValue**](DirectoryVersionHashLookupReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_directory_version_by_sha256_hash

> models::DirectoryVersionHashLookupReturnValue get_directory_version_by_sha256_hash(get_by_sha256_hash_parameters)
Get a directory version by SHA-256 hash.

Gets a directory version DTO by SHA-256 hash.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_by_sha256_hash_parameters** | [**GetBySha256HashParameters**](GetBySha256HashParameters.md) |  | [required] |

### Return type

[**models::DirectoryVersionHashLookupReturnValue**](DirectoryVersionHashLookupReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_directory_versions_by_id

> models::DirectoryVersionListReturnValue list_directory_versions_by_id(get_by_directory_ids_parameters)
List directory versions by id.

Gets directory version DTOs by directory version ids.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_by_directory_ids_parameters** | [**GetByDirectoryIdsParameters**](GetByDirectoryIdsParameters.md) |  | [required] |

### Return type

[**models::DirectoryVersionListReturnValue**](DirectoryVersionListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_directory_versions_recursive

> models::DirectoryVersionListReturnValue list_directory_versions_recursive(get_parameters)
List a directory version and its children.

Gets a directory version and all recursive child directory versions.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_parameters** | [**GetParameters**](GetParameters.md) |  | [required] |

### Return type

[**models::DirectoryVersionListReturnValue**](DirectoryVersionListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## save_directory_versions

> models::DirectoryCommandReturnValue save_directory_versions(save_directory_versions_parameters)
Save directory versions.

Saves a list of directory versions.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**save_directory_versions_parameters** | [**SaveDirectoryVersionsParameters**](SaveDirectoryVersionsParameters.md) |  | [required] |

### Return type

[**models::DirectoryCommandReturnValue**](DirectoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

