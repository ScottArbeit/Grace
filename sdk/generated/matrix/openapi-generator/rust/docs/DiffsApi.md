# \DiffsApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**get_diff**](DiffsApi.md#get_diff) | **POST** /diff/getDiff | Get a diff.
[**get_diff_by_sha256_hash**](DiffsApi.md#get_diff_by_sha256_hash) | **POST** /diff/getDiffBySha256Hash | Get a diff by SHA-256 hash.
[**populate_diff**](DiffsApi.md#populate_diff) | **POST** /diff/populate | Populate a diff actor.



## get_diff

> models::DiffReturnValue get_diff(get_diff_parameters)
Get a diff.

Retrieves the contents of the diff between two directory versions.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_diff_parameters** | [**GetDiffParameters**](GetDiffParameters.md) |  | [required] |

### Return type

[**models::DiffReturnValue**](DiffReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_diff_by_sha256_hash

> models::DiffReturnValue get_diff_by_sha256_hash(get_diff_by_sha256_hash_parameters)
Get a diff by SHA-256 hash.

Retrieves a diff by comparing two directory versions identified by SHA-256 hash.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_diff_by_sha256_hash_parameters** | [**GetDiffBySha256HashParameters**](GetDiffBySha256HashParameters.md) |  | [required] |

### Return type

[**models::DiffReturnValue**](DiffReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## populate_diff

> models::DiffPopulateReturnValue populate_diff(populate_parameters)
Populate a diff actor.

Populates the diff actor without returning the diff. This endpoint is meant to be used when generating the diff through reacting to an event. 

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**populate_parameters** | [**PopulateParameters**](PopulateParameters.md) |  | [required] |

### Return type

[**models::DiffPopulateReturnValue**](DiffPopulateReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

