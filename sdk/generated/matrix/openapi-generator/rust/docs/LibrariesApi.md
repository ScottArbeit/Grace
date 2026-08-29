# \LibrariesApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**add_library**](LibrariesApi.md#add_library) | **POST** /libraries/add | Add one empty normalized Library under an exact configuration version.
[**continue_library_bootstrap**](LibrariesApi.md#continue_library_bootstrap) | **POST** /libraries/bootstrap/continue | Continue one immutable bootstrap baseline page sequence.
[**download_library_content**](LibrariesApi.md#download_library_content) | **GET** /libraries/content/{grantId} | Redeem one authorized short-lived immutable-content read grant.
[**get_library_catalog**](LibrariesApi.md#get_library_catalog) | **POST** /libraries/catalog/get | Get the persisted Library configuration.
[**get_library_changes**](LibrariesApi.md#get_library_changes) | **POST** /libraries/changes/get | Read repository-ordered accepted Library changes after an opaque cursor.
[**get_library_item**](LibrariesApi.md#get_library_item) | **POST** /libraries/items/get | Get one current Library item.
[**get_library_namespace_slot**](LibrariesApi.md#get_library_namespace_slot) | **POST** /libraries/namespace/get-slot | Get one current occupied or remembered-vacant Library namespace slot.
[**get_library_operation**](LibrariesApi.md#get_library_operation) | **POST** /libraries/operations/get | Get the stable receipt for one authorized Library operation identity.
[**get_library_status**](LibrariesApi.md#get_library_status) | **POST** /libraries/status/get | Get content-free Library repository status.
[**list_libraries**](LibrariesApi.md#list_libraries) | **POST** /libraries/list | List the sorted Libraries and their exact configuration version.
[**prepare_library_content**](LibrariesApi.md#prepare_library_content) | **POST** /libraries/content/prepare | Prepare exact immutable bytes for a later Library change.
[**prepare_library_content_read**](LibrariesApi.md#prepare_library_content_read) | **POST** /libraries/content/read | Prepare a one-use read grant for an authorized retained content version.
[**remove_library**](LibrariesApi.md#remove_library) | **POST** /libraries/remove | Remove one empty normalized Library under an exact configuration version.
[**start_library_bootstrap**](LibrariesApi.md#start_library_bootstrap) | **POST** /libraries/bootstrap/start | Start a bounded bootstrap from the current immutable baseline.
[**submit_library_change**](LibrariesApi.md#submit_library_change) | **POST** /libraries/changes/submit | Submit one exact idempotent Library namespace or content change.



## add_library

> models::LibraryCatalogChangeReturnValue add_library(add_library_parameters)
Add one empty normalized Library under an exact configuration version.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**add_library_parameters** | [**AddLibraryParameters**](AddLibraryParameters.md) |  | [required] |

### Return type

[**models::LibraryCatalogChangeReturnValue**](LibraryCatalogChangeReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## continue_library_bootstrap

> models::LibraryBootstrapPageReturnValue continue_library_bootstrap(continue_library_bootstrap_parameters)
Continue one immutable bootstrap baseline page sequence.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**continue_library_bootstrap_parameters** | [**ContinueLibraryBootstrapParameters**](ContinueLibraryBootstrapParameters.md) |  | [required] |

### Return type

[**models::LibraryBootstrapPageReturnValue**](LibraryBootstrapPageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## download_library_content

> std::path::PathBuf download_library_content(grant_id)
Redeem one authorized short-lived immutable-content read grant.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**grant_id** | **String** |  | [required] |

### Return type

[**std::path::PathBuf**](std::path::PathBuf.md)

### Authorization

No authorization required

### HTTP request headers

- **Content-Type**: Not defined
- **Accept**: application/octet-stream, application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_library_catalog

> models::LibraryCatalogReturnValue get_library_catalog(get_library_catalog_parameters)
Get the persisted Library configuration.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_library_catalog_parameters** | [**GetLibraryCatalogParameters**](GetLibraryCatalogParameters.md) |  | [required] |

### Return type

[**models::LibraryCatalogReturnValue**](LibraryCatalogReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_library_changes

> models::LibraryChangePageReturnValue get_library_changes(get_library_changes_parameters)
Read repository-ordered accepted Library changes after an opaque cursor.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_library_changes_parameters** | [**GetLibraryChangesParameters**](GetLibraryChangesParameters.md) |  | [required] |

### Return type

[**models::LibraryChangePageReturnValue**](LibraryChangePageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_library_item

> models::LibraryItemReturnValue get_library_item(get_library_item_parameters)
Get one current Library item.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_library_item_parameters** | [**GetLibraryItemParameters**](GetLibraryItemParameters.md) |  | [required] |

### Return type

[**models::LibraryItemReturnValue**](LibraryItemReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_library_namespace_slot

> models::LibraryNamespaceSlotReturnValue get_library_namespace_slot(get_library_namespace_slot_parameters)
Get one current occupied or remembered-vacant Library namespace slot.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_library_namespace_slot_parameters** | [**GetLibraryNamespaceSlotParameters**](GetLibraryNamespaceSlotParameters.md) |  | [required] |

### Return type

[**models::LibraryNamespaceSlotReturnValue**](LibraryNamespaceSlotReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_library_operation

> models::LibraryOperationReceiptReturnValue get_library_operation(get_library_operation_parameters)
Get the stable receipt for one authorized Library operation identity.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_library_operation_parameters** | [**GetLibraryOperationParameters**](GetLibraryOperationParameters.md) |  | [required] |

### Return type

[**models::LibraryOperationReceiptReturnValue**](LibraryOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_library_status

> models::LibraryStatusReturnValue get_library_status(get_library_status_parameters)
Get content-free Library repository status.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_library_status_parameters** | [**GetLibraryStatusParameters**](GetLibraryStatusParameters.md) |  | [required] |

### Return type

[**models::LibraryStatusReturnValue**](LibraryStatusReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_libraries

> models::LibraryCatalogReturnValue list_libraries(list_libraries_parameters)
List the sorted Libraries and their exact configuration version.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**list_libraries_parameters** | [**ListLibrariesParameters**](ListLibrariesParameters.md) |  | [required] |

### Return type

[**models::LibraryCatalogReturnValue**](LibraryCatalogReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## prepare_library_content

> models::LibraryPreparedContentReturnValue prepare_library_content(prepare_library_content_parameters)
Prepare exact immutable bytes for a later Library change.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**prepare_library_content_parameters** | [**PrepareLibraryContentParameters**](PrepareLibraryContentParameters.md) |  | [required] |

### Return type

[**models::LibraryPreparedContentReturnValue**](LibraryPreparedContentReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## prepare_library_content_read

> models::LibraryContentReadGrantReturnValue prepare_library_content_read(prepare_library_content_read_parameters)
Prepare a one-use read grant for an authorized retained content version.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**prepare_library_content_read_parameters** | [**PrepareLibraryContentReadParameters**](PrepareLibraryContentReadParameters.md) |  | [required] |

### Return type

[**models::LibraryContentReadGrantReturnValue**](LibraryContentReadGrantReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## remove_library

> models::LibraryCatalogChangeReturnValue remove_library(remove_library_parameters)
Remove one empty normalized Library under an exact configuration version.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**remove_library_parameters** | [**RemoveLibraryParameters**](RemoveLibraryParameters.md) |  | [required] |

### Return type

[**models::LibraryCatalogChangeReturnValue**](LibraryCatalogChangeReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## start_library_bootstrap

> models::LibraryBootstrapPageReturnValue start_library_bootstrap(start_library_bootstrap_parameters)
Start a bounded bootstrap from the current immutable baseline.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**start_library_bootstrap_parameters** | [**StartLibraryBootstrapParameters**](StartLibraryBootstrapParameters.md) |  | [required] |

### Return type

[**models::LibraryBootstrapPageReturnValue**](LibraryBootstrapPageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## submit_library_change

> models::LibraryOperationReceiptReturnValue submit_library_change(submit_library_change_parameters)
Submit one exact idempotent Library namespace or content change.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**submit_library_change_parameters** | [**SubmitLibraryChangeParameters**](SubmitLibraryChangeParameters.md) |  | [required] |

### Return type

[**models::LibraryOperationReceiptReturnValue**](LibraryOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

