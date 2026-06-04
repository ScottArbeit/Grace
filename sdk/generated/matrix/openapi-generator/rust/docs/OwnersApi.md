# \OwnersApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_owner**](OwnersApi.md#create_owner) | **POST** /owner/create | Create an owner.
[**delete_owner**](OwnersApi.md#delete_owner) | **POST** /owner/delete | Delete an owner.
[**get_owner**](OwnersApi.md#get_owner) | **POST** /owner/get | Get an owner.
[**list_owner_organizations**](OwnersApi.md#list_owner_organizations) | **POST** /owner/listOrganizations | List the organizations for an owner.
[**set_owner_description**](OwnersApi.md#set_owner_description) | **POST** /owner/setDescription | Set the owner's description.
[**set_owner_name**](OwnersApi.md#set_owner_name) | **POST** /owner/setName | Set the name of an owner.
[**set_owner_search_visibility**](OwnersApi.md#set_owner_search_visibility) | **POST** /owner/setSearchVisibility | Set owner search visibility.
[**set_owner_type**](OwnersApi.md#set_owner_type) | **POST** /owner/setType | Set the owner type.
[**undelete_owner**](OwnersApi.md#undelete_owner) | **POST** /owner/undelete | Undelete a previously deleted owner.



## create_owner

> models::OwnerCommandReturnValue create_owner(create_owner_parameters)
Create an owner.

Creates an owner with the specified id and name.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_owner_parameters** | [**CreateOwnerParameters**](CreateOwnerParameters.md) |  | [required] |

### Return type

[**models::OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## delete_owner

> models::OwnerCommandReturnValue delete_owner(delete_owner_parameters)
Delete an owner.

Logically deletes an owner that exists and has not already been deleted.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**delete_owner_parameters** | [**DeleteOwnerParameters**](DeleteOwnerParameters.md) |  | [required] |

### Return type

[**models::OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_owner

> models::OwnerReturnValue get_owner(get_owner_parameters)
Get an owner.

Gets owner metadata.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_owner_parameters** | [**GetOwnerParameters**](GetOwnerParameters.md) |  | [required] |

### Return type

[**models::OwnerReturnValue**](OwnerReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_owner_organizations

> std::collections::HashMap<String, String> list_owner_organizations(list_organizations_parameters)
List the organizations for an owner.

Lists organizations associated with the owner.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**list_organizations_parameters** | [**ListOrganizationsParameters**](ListOrganizationsParameters.md) |  | [required] |

### Return type

**std::collections::HashMap<String, String>**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_owner_description

> models::OwnerCommandReturnValue set_owner_description(set_owner_description_parameters)
Set the owner's description.

Sets the owner description.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_owner_description_parameters** | [**SetOwnerDescriptionParameters**](SetOwnerDescriptionParameters.md) |  | [required] |

### Return type

[**models::OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_owner_name

> models::OwnerCommandReturnValue set_owner_name(set_owner_name_parameters)
Set the name of an owner.

Renames an owner that exists and has not been deleted.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_owner_name_parameters** | [**SetOwnerNameParameters**](SetOwnerNameParameters.md) |  | [required] |

### Return type

[**models::OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_owner_search_visibility

> models::OwnerCommandReturnValue set_owner_search_visibility(set_owner_search_visibility_parameters)
Set owner search visibility.

Sets whether the owner appears in search results.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_owner_search_visibility_parameters** | [**SetOwnerSearchVisibilityParameters**](SetOwnerSearchVisibilityParameters.md) |  | [required] |

### Return type

[**models::OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_owner_type

> models::OwnerCommandReturnValue set_owner_type(set_owner_type_parameters)
Set the owner type.

Sets the owner type to a public or private value accepted by the server.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_owner_type_parameters** | [**SetOwnerTypeParameters**](SetOwnerTypeParameters.md) |  | [required] |

### Return type

[**models::OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## undelete_owner

> models::OwnerCommandReturnValue undelete_owner(undelete_owner_parameters)
Undelete a previously deleted owner.

Restores a logically deleted owner.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**undelete_owner_parameters** | [**UndeleteOwnerParameters**](UndeleteOwnerParameters.md) |  | [required] |

### Return type

[**models::OwnerCommandReturnValue**](OwnerCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

