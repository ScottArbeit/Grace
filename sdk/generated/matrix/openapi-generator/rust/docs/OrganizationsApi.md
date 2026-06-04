# \OrganizationsApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_organization**](OrganizationsApi.md#create_organization) | **POST** /organization/create | Create an organization.
[**delete_organization**](OrganizationsApi.md#delete_organization) | **POST** /organization/delete | Delete an organization.
[**get_organization**](OrganizationsApi.md#get_organization) | **POST** /organization/get | Get an organization.
[**list_organization_repositories**](OrganizationsApi.md#list_organization_repositories) | **POST** /organization/listRepositories | List repositories of an organization.
[**set_organization_description**](OrganizationsApi.md#set_organization_description) | **POST** /organization/setDescription | Set the organization's description.
[**set_organization_name**](OrganizationsApi.md#set_organization_name) | **POST** /organization/setName | Set the name of an organization.
[**set_organization_search_visibility**](OrganizationsApi.md#set_organization_search_visibility) | **POST** /organization/setSearchVisibility | Set organization search visibility.
[**set_organization_type**](OrganizationsApi.md#set_organization_type) | **POST** /organization/setType | Set the organization type.
[**undelete_organization**](OrganizationsApi.md#undelete_organization) | **POST** /organization/undelete | Undelete a previously deleted organization.



## create_organization

> models::OrganizationCommandReturnValue create_organization(create_organization_parameters)
Create an organization.

Creates an organization under an owner.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_organization_parameters** | [**CreateOrganizationParameters**](CreateOrganizationParameters.md) |  | [required] |

### Return type

[**models::OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## delete_organization

> models::OrganizationCommandReturnValue delete_organization(delete_organization_parameters)
Delete an organization.

Logically deletes an organization that exists and has not already been deleted.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**delete_organization_parameters** | [**DeleteOrganizationParameters**](DeleteOrganizationParameters.md) |  | [required] |

### Return type

[**models::OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_organization

> models::OrganizationReturnValue get_organization(get_organization_parameters)
Get an organization.

Gets organization metadata.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**get_organization_parameters** | [**GetOrganizationParameters**](GetOrganizationParameters.md) |  | [required] |

### Return type

[**models::OrganizationReturnValue**](OrganizationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_organization_repositories

> std::collections::HashMap<String, String> list_organization_repositories(list_repositories_parameters)
List repositories of an organization.

Lists repositories associated with the organization.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**list_repositories_parameters** | [**ListRepositoriesParameters**](ListRepositoriesParameters.md) |  | [required] |

### Return type

**std::collections::HashMap<String, String>**

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_organization_description

> models::OrganizationCommandReturnValue set_organization_description(set_organization_description_parameters)
Set the organization's description.

Sets the organization description.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_organization_description_parameters** | [**SetOrganizationDescriptionParameters**](SetOrganizationDescriptionParameters.md) |  | [required] |

### Return type

[**models::OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_organization_name

> models::OrganizationCommandReturnValue set_organization_name(set_organization_name_parameters)
Set the name of an organization.

Renames an organization that exists and has not been deleted.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_organization_name_parameters** | [**SetOrganizationNameParameters**](SetOrganizationNameParameters.md) |  | [required] |

### Return type

[**models::OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_organization_search_visibility

> models::OrganizationCommandReturnValue set_organization_search_visibility(set_organization_search_visibility_parameters)
Set organization search visibility.

Sets whether the organization appears in search results.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_organization_search_visibility_parameters** | [**SetOrganizationSearchVisibilityParameters**](SetOrganizationSearchVisibilityParameters.md) |  | [required] |

### Return type

[**models::OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## set_organization_type

> models::OrganizationCommandReturnValue set_organization_type(set_organization_type_parameters)
Set the organization type.

Sets the organization type to a public or private value accepted by the server.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**set_organization_type_parameters** | [**SetOrganizationTypeParameters**](SetOrganizationTypeParameters.md) |  | [required] |

### Return type

[**models::OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## undelete_organization

> models::OrganizationCommandReturnValue undelete_organization(undelete_organization_parameters)
Undelete a previously deleted organization.

Restores a logically deleted organization.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**undelete_organization_parameters** | [**UndeleteOrganizationParameters**](UndeleteOrganizationParameters.md) |  | [required] |

### Return type

[**models::OrganizationCommandReturnValue**](OrganizationCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

