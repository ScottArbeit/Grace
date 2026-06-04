# RepositoryDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**repository_id** | Option<**uuid::Uuid**> |  | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_name** | Option<**String**> |  | [optional]
**object_storage_provider** | Option<[**models::ObjectStorageProvider**](ObjectStorageProvider.md)> |  | [optional]
**storage_account_name** | Option<**String**> |  | [optional]
**storage_container_name** | Option<**String**> |  | [optional]
**repository_visibility** | Option<[**models::RepositoryVisibility**](RepositoryVisibility.md)> |  | [optional]
**repository_status** | Option<[**models::RepositoryStatus**](RepositoryStatus.md)> |  | [optional]
**branches** | Option<**Vec<String>**> |  | [optional]
**default_server_api_version** | Option<**String**> |  | [optional]
**default_branch_name** | Option<**String**> |  | [optional]
**save_days** | Option<**f64**> |  | [optional]
**checkpoint_days** | Option<**f64**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


