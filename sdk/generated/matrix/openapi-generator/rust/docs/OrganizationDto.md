# OrganizationDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_name** | Option<**String**> |  | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_type** | Option<[**models::OrganizationType**](OrganizationType.md)> |  | [optional]
**description** | Option<**String**> |  | [optional]
**search_visibility** | Option<[**models::SearchVisibility**](SearchVisibility.md)> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**updated_at** | Option<[**models::BranchDtoUpdatedAt**](BranchDtoUpdatedAt.md)> |  | [optional]
**deleted_at** | Option<[**models::BranchDtoUpdatedAt**](BranchDtoUpdatedAt.md)> |  | [optional]
**delete_reason** | Option<**String**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


