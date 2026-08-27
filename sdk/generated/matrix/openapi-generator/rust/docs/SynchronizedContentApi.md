# \SynchronizedContentApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**add_synchronized_root**](SynchronizedContentApi.md#add_synchronized_root) | **POST** /sync/roots/add | Add one empty normalized synchronized root under an exact configuration version.
[**continue_synchronized_bootstrap**](SynchronizedContentApi.md#continue_synchronized_bootstrap) | **POST** /sync/bootstrap/continue | Continue one immutable bootstrap baseline page sequence.
[**download_synchronized_content**](SynchronizedContentApi.md#download_synchronized_content) | **GET** /sync/content/{grantId} | Redeem one authorized short-lived immutable-content read grant.
[**get_synchronized_deltas**](SynchronizedContentApi.md#get_synchronized_deltas) | **POST** /sync/deltas/get | Read repository-ordered accepted synchronized mutations after an opaque cursor.
[**get_synchronized_item**](SynchronizedContentApi.md#get_synchronized_item) | **POST** /sync/items/get | Get one current synchronized item.
[**get_synchronized_namespace_slot**](SynchronizedContentApi.md#get_synchronized_namespace_slot) | **POST** /sync/namespace/get-slot | Get one current occupied or remembered-vacant synchronized namespace slot.
[**get_synchronized_operation**](SynchronizedContentApi.md#get_synchronized_operation) | **POST** /sync/operations/get | Get the stable receipt for one authorized synchronized operation identity.
[**get_synchronized_root_configuration**](SynchronizedContentApi.md#get_synchronized_root_configuration) | **POST** /sync/roots/get | Get the persisted synchronized-root configuration.
[**get_synchronized_status**](SynchronizedContentApi.md#get_synchronized_status) | **POST** /sync/status/get | Get content-free synchronized repository status.
[**list_synchronized_roots**](SynchronizedContentApi.md#list_synchronized_roots) | **POST** /sync/roots/list | List the sorted synchronized roots and their exact configuration version.
[**prepare_synchronized_content**](SynchronizedContentApi.md#prepare_synchronized_content) | **POST** /sync/content/prepare | Prepare exact immutable bytes for a later synchronized mutation.
[**prepare_synchronized_content_read**](SynchronizedContentApi.md#prepare_synchronized_content_read) | **POST** /sync/content/read | Prepare a one-use read grant for an authorized retained content version.
[**remove_synchronized_root**](SynchronizedContentApi.md#remove_synchronized_root) | **POST** /sync/roots/remove | Remove one empty normalized synchronized root under an exact configuration version.
[**start_synchronized_bootstrap**](SynchronizedContentApi.md#start_synchronized_bootstrap) | **POST** /sync/bootstrap/start | Start a bounded bootstrap from the current immutable baseline.
[**submit_synchronized_mutation**](SynchronizedContentApi.md#submit_synchronized_mutation) | **POST** /sync/mutations/submit | Submit one exact idempotent synchronized namespace or content mutation.



## add_synchronized_root

> models::SynchronizedRootMutationReturnValue add_synchronized_root(add_synchronized_root_parameters)
Add one empty normalized synchronized root under an exact configuration version.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**add_synchronized_root_parameters** | [**AddSynchronizedRootParameters**](AddSynchronizedRootParameters.md) |  | [required] |

### Return type

[**models::SynchronizedRootMutationReturnValue**](SynchronizedRootMutationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## continue_synchronized_bootstrap

> models::SynchronizedBootstrapPageReturnValue continue_synchronized_bootstrap(continue_synchronized_bootstrap_parameters)
Continue one immutable bootstrap baseline page sequence.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**continue_synchronized_bootstrap_parameters** | [**ContinueSynchronizedBootstrapParameters**](ContinueSynchronizedBootstrapParameters.md) |  | [required] |

### Return type

[**models::SynchronizedBootstrapPageReturnValue**](SynchronizedBootstrapPageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## download_synchronized_content

> std::path::PathBuf download_synchronized_content(grant_id)
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


## get_synchronized_deltas

> models::SynchronizedDeltaReturnValue get_synchronized_deltas(get_synchronized_deltas_parameters)
Read repository-ordered accepted synchronized mutations after an opaque cursor.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_synchronized_deltas_parameters** | [**GetSynchronizedDeltasParameters**](GetSynchronizedDeltasParameters.md) |  | [required] |

### Return type

[**models::SynchronizedDeltaReturnValue**](SynchronizedDeltaReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_synchronized_item

> models::SynchronizedItemReturnValue get_synchronized_item(get_synchronized_item_parameters)
Get one current synchronized item.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_synchronized_item_parameters** | [**GetSynchronizedItemParameters**](GetSynchronizedItemParameters.md) |  | [required] |

### Return type

[**models::SynchronizedItemReturnValue**](SynchronizedItemReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_synchronized_namespace_slot

> models::SynchronizedNamespaceSlotReturnValue get_synchronized_namespace_slot(get_synchronized_namespace_slot_parameters)
Get one current occupied or remembered-vacant synchronized namespace slot.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_synchronized_namespace_slot_parameters** | [**GetSynchronizedNamespaceSlotParameters**](GetSynchronizedNamespaceSlotParameters.md) |  | [required] |

### Return type

[**models::SynchronizedNamespaceSlotReturnValue**](SynchronizedNamespaceSlotReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_synchronized_operation

> models::SynchronizedOperationReceiptReturnValue get_synchronized_operation(get_synchronized_operation_parameters)
Get the stable receipt for one authorized synchronized operation identity.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_synchronized_operation_parameters** | [**GetSynchronizedOperationParameters**](GetSynchronizedOperationParameters.md) |  | [required] |

### Return type

[**models::SynchronizedOperationReceiptReturnValue**](SynchronizedOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_synchronized_root_configuration

> models::SynchronizedRootConfigurationReturnValue get_synchronized_root_configuration(get_synchronized_root_configuration_parameters)
Get the persisted synchronized-root configuration.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_synchronized_root_configuration_parameters** | [**GetSynchronizedRootConfigurationParameters**](GetSynchronizedRootConfigurationParameters.md) |  | [required] |

### Return type

[**models::SynchronizedRootConfigurationReturnValue**](SynchronizedRootConfigurationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_synchronized_status

> models::SynchronizedStatusReturnValue get_synchronized_status(get_synchronized_status_parameters)
Get content-free synchronized repository status.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_synchronized_status_parameters** | [**GetSynchronizedStatusParameters**](GetSynchronizedStatusParameters.md) |  | [required] |

### Return type

[**models::SynchronizedStatusReturnValue**](SynchronizedStatusReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_synchronized_roots

> models::SynchronizedRootConfigurationReturnValue list_synchronized_roots(list_synchronized_roots_parameters)
List the sorted synchronized roots and their exact configuration version.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**list_synchronized_roots_parameters** | [**ListSynchronizedRootsParameters**](ListSynchronizedRootsParameters.md) |  | [required] |

### Return type

[**models::SynchronizedRootConfigurationReturnValue**](SynchronizedRootConfigurationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## prepare_synchronized_content

> models::SynchronizedPreparedContentReturnValue prepare_synchronized_content(prepare_synchronized_content_parameters)
Prepare exact immutable bytes for a later synchronized mutation.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**prepare_synchronized_content_parameters** | [**PrepareSynchronizedContentParameters**](PrepareSynchronizedContentParameters.md) |  | [required] |

### Return type

[**models::SynchronizedPreparedContentReturnValue**](SynchronizedPreparedContentReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## prepare_synchronized_content_read

> models::SynchronizedContentReadGrantReturnValue prepare_synchronized_content_read(prepare_synchronized_content_read_parameters)
Prepare a one-use read grant for an authorized retained content version.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**prepare_synchronized_content_read_parameters** | [**PrepareSynchronizedContentReadParameters**](PrepareSynchronizedContentReadParameters.md) |  | [required] |

### Return type

[**models::SynchronizedContentReadGrantReturnValue**](SynchronizedContentReadGrantReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## remove_synchronized_root

> models::SynchronizedRootMutationReturnValue remove_synchronized_root(remove_synchronized_root_parameters)
Remove one empty normalized synchronized root under an exact configuration version.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**remove_synchronized_root_parameters** | [**RemoveSynchronizedRootParameters**](RemoveSynchronizedRootParameters.md) |  | [required] |

### Return type

[**models::SynchronizedRootMutationReturnValue**](SynchronizedRootMutationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## start_synchronized_bootstrap

> models::SynchronizedBootstrapPageReturnValue start_synchronized_bootstrap(start_synchronized_bootstrap_parameters)
Start a bounded bootstrap from the current immutable baseline.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**start_synchronized_bootstrap_parameters** | [**StartSynchronizedBootstrapParameters**](StartSynchronizedBootstrapParameters.md) |  | [required] |

### Return type

[**models::SynchronizedBootstrapPageReturnValue**](SynchronizedBootstrapPageReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## submit_synchronized_mutation

> models::SynchronizedOperationReceiptReturnValue submit_synchronized_mutation(submit_synchronized_mutation_parameters)
Submit one exact idempotent synchronized namespace or content mutation.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**submit_synchronized_mutation_parameters** | [**SubmitSynchronizedMutationParameters**](SubmitSynchronizedMutationParameters.md) |  | [required] |

### Return type

[**models::SynchronizedOperationReceiptReturnValue**](SynchronizedOperationReceiptReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

