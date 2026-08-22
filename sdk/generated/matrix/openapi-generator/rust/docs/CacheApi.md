# \CacheApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**prepare_directory_version_zip**](CacheApi.md#prepare_directory_version_zip) | **POST** /cache/prepareDirectoryVersionZip | Prepare one Server-approved DirectoryVersion ZIP fill.
[**redeem_directory_version_zip_fill**](CacheApi.md#redeem_directory_version_zip_fill) | **POST** /cache/redeemDirectoryVersionZipFill | Redeem one permit and Cache process signature for a read-only ZIP source.



## prepare_directory_version_zip

> models::DirectoryVersionZipPreparation prepare_directory_version_zip(prepare_directory_version_zip_parameters)
Prepare one Server-approved DirectoryVersion ZIP fill.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**prepare_directory_version_zip_parameters** | [**PrepareDirectoryVersionZipParameters**](PrepareDirectoryVersionZipParameters.md) |  | [required] |

### Return type

[**models::DirectoryVersionZipPreparation**](DirectoryVersionZipPreparation.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## redeem_directory_version_zip_fill

> models::DirectoryVersionZipFillSource redeem_directory_version_zip_fill(redeem_directory_version_zip_fill_parameters)
Redeem one permit and Cache process signature for a read-only ZIP source.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**redeem_directory_version_zip_fill_parameters** | [**RedeemDirectoryVersionZipFillParameters**](RedeemDirectoryVersionZipFillParameters.md) |  | [required] |

### Return type

[**models::DirectoryVersionZipFillSource**](DirectoryVersionZipFillSource.md)

### Authorization

No authorization required

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

