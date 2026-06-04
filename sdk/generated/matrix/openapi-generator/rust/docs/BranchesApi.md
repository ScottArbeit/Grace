# \BranchesApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
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



## checkpoint_branch

> models::BranchCommandReturnValue checkpoint_branch(create_reference_parameters)
Checkpoint the current branch content.

Creates a checkpoint reference pointing to the current root directory version in the branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_reference_parameters** | [**CreateReferenceParameters**](CreateReferenceParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## commit_branch

> models::BranchCommandReturnValue commit_branch(create_reference_parameters)
Commit the current branch content.

Creates a commit reference pointing to the current root directory version in the branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_reference_parameters** | [**CreateReferenceParameters**](CreateReferenceParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## create_branch

> models::BranchCommandReturnValue create_branch(create_branch_parameters)
Create a branch.

Creates a branch with the specified name, based on the specified parent branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_branch_parameters** | [**CreateBranchParameters**](CreateBranchParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## delete_branch

> models::BranchCommandReturnValue delete_branch(delete_branch_parameters)
Delete a branch.

Deletes the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**delete_branch_parameters** | [**DeleteBranchParameters**](DeleteBranchParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## enable_branch_checkpoint

> models::BranchCommandReturnValue enable_branch_checkpoint(enable_feature_parameters)
Enable or disable checkpoint references.

Enables or disables checkpoint for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**enable_feature_parameters** | [**EnableFeatureParameters**](EnableFeatureParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## enable_branch_commit

> models::BranchCommandReturnValue enable_branch_commit(enable_feature_parameters)
Enable or disable commit references.

Enables or disables commit for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**enable_feature_parameters** | [**EnableFeatureParameters**](EnableFeatureParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## enable_branch_promotion

> models::BranchCommandReturnValue enable_branch_promotion(enable_feature_parameters)
Enable or disable promotion references.

Enables or disables promotion for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**enable_feature_parameters** | [**EnableFeatureParameters**](EnableFeatureParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## enable_branch_save

> models::BranchCommandReturnValue enable_branch_save(enable_feature_parameters)
Enable or disable save references.

Enables or disables save for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**enable_feature_parameters** | [**EnableFeatureParameters**](EnableFeatureParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## enable_branch_tag

> models::BranchCommandReturnValue enable_branch_tag(enable_feature_parameters)
Enable or disable tag references.

Enables or disables tag for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**enable_feature_parameters** | [**EnableFeatureParameters**](EnableFeatureParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_branch

> models::BranchReturnValue get_branch(get_branch_parameters)
Get a branch.

Gets the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_branch_parameters** | [**GetBranchParameters**](GetBranchParameters.md) |  | [required] |

### Return type

[**models::BranchReturnValue**](BranchReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_branch_reference

> models::ReferenceReturnValue get_branch_reference(get_reference_parameters)
Get a branch reference.

Gets the specified reference from the branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_reference_parameters** | [**GetReferenceParameters**](GetReferenceParameters.md) |  | [required] |

### Return type

[**models::ReferenceReturnValue**](ReferenceReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_parent_branch

> models::BranchReturnValue get_parent_branch(get_branch_parameters)
Get the parent branch.

Gets the parent branch of the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_branch_parameters** | [**GetBranchParameters**](GetBranchParameters.md) |  | [required] |

### Return type

[**models::BranchReturnValue**](BranchReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_branch_checkpoints

> models::ReferenceListReturnValue list_branch_checkpoints(get_references_parameters)
List branch checkpoints.

Gets the checkpoint references for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md) |  | [required] |

### Return type

[**models::ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_branch_commits

> models::ReferenceListReturnValue list_branch_commits(get_references_parameters)
List branch commits.

Gets the commit references for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md) |  | [required] |

### Return type

[**models::ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_branch_promotions

> models::ReferenceListReturnValue list_branch_promotions(get_references_parameters)
List branch promotions.

Gets the promotion references for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md) |  | [required] |

### Return type

[**models::ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_branch_references

> models::ReferenceListReturnValue list_branch_references(get_references_parameters)
List branch references.

Gets the references for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md) |  | [required] |

### Return type

[**models::ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_branch_saves

> models::ReferenceListReturnValue list_branch_saves(get_references_parameters)
List branch saves.

Gets the save references for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md) |  | [required] |

### Return type

[**models::ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_branch_tags

> models::ReferenceListReturnValue list_branch_tags(get_references_parameters)
List branch tags.

Gets the tag references for the specified branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_references_parameters** | [**GetReferencesParameters**](GetReferencesParameters.md) |  | [required] |

### Return type

[**models::ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## promote_branch

> models::BranchCommandReturnValue promote_branch(create_reference_parameters)
Promote the current branch content.

Creates a promotion reference in the parent of the specified branch, based on the current directory version.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_reference_parameters** | [**CreateReferenceParameters**](CreateReferenceParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## rebase_branch

> models::BranchCommandReturnValue rebase_branch(rebase_parameters)
Rebase a branch.

Rebases a branch on the specified reference from its parent branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**rebase_parameters** | [**RebaseParameters**](RebaseParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## save_branch

> models::BranchCommandReturnValue save_branch(create_reference_parameters)
Save the current branch content.

Creates a save reference pointing to the current root directory version in the branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_reference_parameters** | [**CreateReferenceParameters**](CreateReferenceParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## tag_branch

> models::BranchCommandReturnValue tag_branch(create_reference_parameters)
Tag the current branch content.

Creates a tag reference pointing to the current root directory version in the branch.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_reference_parameters** | [**CreateReferenceParameters**](CreateReferenceParameters.md) |  | [required] |

### Return type

[**models::BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

