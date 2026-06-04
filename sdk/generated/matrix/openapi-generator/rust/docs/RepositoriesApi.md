# \RepositoriesApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_repository**](RepositoriesApi.md#create_repository) | **POST** /repository/create | Create a new repository.
[**delete_repository**](RepositoriesApi.md#delete_repository) | **POST** /repository/delete | Delete a repository.
[**get_repository**](RepositoriesApi.md#get_repository) | **POST** /repository/get | Get a repository.
[**list_repository_branches**](RepositoriesApi.md#list_repository_branches) | **POST** /repository/getBranches | List repository branches.
[**list_repository_branches_by_branch_id**](RepositoriesApi.md#list_repository_branches_by_branch_id) | **POST** /repository/getBranchesByBranchId | List branches by branch id.
[**list_repository_references_by_reference_id**](RepositoriesApi.md#list_repository_references_by_reference_id) | **POST** /repository/getReferencesByReferenceId | List references by reference id.
[**repository_exists**](RepositoriesApi.md#repository_exists) | **POST** /repository/exists | Check whether a repository exists.
[**repository_is_empty**](RepositoriesApi.md#repository_is_empty) | **POST** /repository/isEmpty | Check whether a repository is empty.
[**set_repository_checkpoint_days**](RepositoriesApi.md#set_repository_checkpoint_days) | **POST** /repository/setCheckpointDays | Set checkpoint retention.
[**set_repository_default_server_api_version**](RepositoriesApi.md#set_repository_default_server_api_version) | **POST** /repository/setDefaultServerApiVersion | Set the default server API version.
[**set_repository_description**](RepositoriesApi.md#set_repository_description) | **POST** /repository/setDescription | Set repository description.
[**set_repository_name**](RepositoriesApi.md#set_repository_name) | **POST** /repository/setName | Set the name of a repository.
[**set_repository_record_saves**](RepositoriesApi.md#set_repository_record_saves) | **POST** /repository/setRecordSaves | Set whether saves are recorded.
[**set_repository_save_days**](RepositoriesApi.md#set_repository_save_days) | **POST** /repository/setSaveDays | Set save retention.
[**set_repository_status**](RepositoriesApi.md#set_repository_status) | **POST** /repository/setStatus | Set repository status.
[**set_repository_visibility**](RepositoriesApi.md#set_repository_visibility) | **POST** /repository/setVisibility | Set repository visibility.
[**undelete_repository**](RepositoriesApi.md#undelete_repository) | **POST** /repository/undelete | Undelete a previously deleted repository.



## create_repository

> models::RepositoryCommandReturnValue create_repository(create_repository_parameters)
Create a new repository.

Creates a repository in an organization.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_repository_parameters** | [**CreateRepositoryParameters**](CreateRepositoryParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## delete_repository

> models::RepositoryCommandReturnValue delete_repository(delete_repository_parameters)
Delete a repository.

Logically deletes a repository that exists and has not already been deleted.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**delete_repository_parameters** | [**DeleteRepositoryParameters**](DeleteRepositoryParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_repository

> models::RepositoryReturnValue get_repository(repository_parameters)
Get a repository.

Gets repository metadata.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**repository_parameters** | [**RepositoryParameters**](RepositoryParameters.md) |  | [required] |

### Return type

[**models::RepositoryReturnValue**](RepositoryReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_repository_branches

> models::RepositoryBranchesReturnValue list_repository_branches(get_branches_parameters)
List repository branches.

Gets branches for the repository.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_branches_parameters** | [**GetBranchesParameters**](GetBranchesParameters.md) |  | [required] |

### Return type

[**models::RepositoryBranchesReturnValue**](RepositoryBranchesReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_repository_branches_by_branch_id

> models::RepositoryBranchesReturnValue list_repository_branches_by_branch_id(get_branches_by_branch_id_parameters)
List branches by branch id.

Gets branches in the repository by branch ids.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_branches_by_branch_id_parameters** | [**GetBranchesByBranchIdParameters**](GetBranchesByBranchIdParameters.md) |  | [required] |

### Return type

[**models::RepositoryBranchesReturnValue**](RepositoryBranchesReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_repository_references_by_reference_id

> models::RepositoryReferencesReturnValue list_repository_references_by_reference_id(get_references_by_reference_id_parameters)
List references by reference id.

Gets references in the repository by reference ids.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_references_by_reference_id_parameters** | [**GetReferencesByReferenceIdParameters**](GetReferencesByReferenceIdParameters.md) |  | [required] |

### Return type

[**models::RepositoryReferencesReturnValue**](RepositoryReferencesReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## repository_exists

> models::RepositoryBooleanReturnValue repository_exists(repository_parameters)
Check whether a repository exists.

Checks whether a repository exists for the supplied selector values.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**repository_parameters** | [**RepositoryParameters**](RepositoryParameters.md) |  | [required] |

### Return type

[**models::RepositoryBooleanReturnValue**](RepositoryBooleanReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## repository_is_empty

> models::RepositoryBooleanReturnValue repository_is_empty(is_empty_parameters)
Check whether a repository is empty.

Checks whether a repository has no content yet.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**is_empty_parameters** | [**IsEmptyParameters**](IsEmptyParameters.md) |  | [required] |

### Return type

[**models::RepositoryBooleanReturnValue**](RepositoryBooleanReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_repository_checkpoint_days

> models::RepositoryCommandReturnValue set_repository_checkpoint_days(set_checkpoint_days_parameters)
Set checkpoint retention.

Sets the number of days to keep checkpoints in the repository.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_checkpoint_days_parameters** | [**SetCheckpointDaysParameters**](SetCheckpointDaysParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_repository_default_server_api_version

> models::RepositoryCommandReturnValue set_repository_default_server_api_version(set_default_server_api_version_parameters)
Set the default server API version.

Sets the default server API version clients should use for the repository.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_default_server_api_version_parameters** | [**SetDefaultServerApiVersionParameters**](SetDefaultServerApiVersionParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_repository_description

> models::RepositoryCommandReturnValue set_repository_description(set_repository_description_parameters)
Set repository description.

Sets the repository description.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_repository_description_parameters** | [**SetRepositoryDescriptionParameters**](SetRepositoryDescriptionParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_repository_name

> models::RepositoryCommandReturnValue set_repository_name(set_repository_name_parameters)
Set the name of a repository.

Renames a repository that exists and has not been deleted.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_repository_name_parameters** | [**SetRepositoryNameParameters**](SetRepositoryNameParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_repository_record_saves

> models::RepositoryCommandReturnValue set_repository_record_saves(record_saves_parameters)
Set whether saves are recorded.

Sets the repository default for whether save references should be kept.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**record_saves_parameters** | [**RecordSavesParameters**](RecordSavesParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_repository_save_days

> models::RepositoryCommandReturnValue set_repository_save_days(set_save_days_parameters)
Set save retention.

Sets the number of days to keep saves in the repository.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_save_days_parameters** | [**SetSaveDaysParameters**](SetSaveDaysParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_repository_status

> models::RepositoryCommandReturnValue set_repository_status(set_repository_status_parameters)
Set repository status.

Sets the repository operational status.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_repository_status_parameters** | [**SetRepositoryStatusParameters**](SetRepositoryStatusParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_repository_visibility

> models::RepositoryCommandReturnValue set_repository_visibility(set_repository_visibility_parameters)
Set repository visibility.

Sets whether the repository is public or private.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_repository_visibility_parameters** | [**SetRepositoryVisibilityParameters**](SetRepositoryVisibilityParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## undelete_repository

> models::RepositoryCommandReturnValue undelete_repository(undelete_repository_parameters)
Undelete a previously deleted repository.

Restores a logically deleted repository.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**undelete_repository_parameters** | [**UndeleteRepositoryParameters**](UndeleteRepositoryParameters.md) |  | [required] |

### Return type

[**models::RepositoryCommandReturnValue**](RepositoryCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

