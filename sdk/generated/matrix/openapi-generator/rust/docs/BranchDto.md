# BranchDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**branch_id** | Option<**uuid::Uuid**> |  | [optional]
**branch_name** | Option<**String**> |  | [optional]
**parent_branch_id** | Option<**uuid::Uuid**> |  | [optional]
**based_on** | Option<**uuid::Uuid**> |  | [optional]
**repository_id** | Option<**uuid::Uuid**> |  | [optional]
**user_id** | Option<**String**> |  | [optional]
**promotion_enabled** | Option<**bool**> |  | [optional]
**commit_enabled** | Option<**bool**> |  | [optional]
**checkpoint_enabled** | Option<**bool**> |  | [optional]
**save_enabled** | Option<**bool**> |  | [optional]
**tag_enabled** | Option<**bool**> |  | [optional]
**latest_promotion** | Option<**uuid::Uuid**> |  | [optional]
**latest_commit** | Option<**uuid::Uuid**> |  | [optional]
**latest_checkpoint** | Option<**uuid::Uuid**> |  | [optional]
**latest_save** | Option<**uuid::Uuid**> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**updated_at** | Option<[**models::BranchDtoUpdatedAt**](BranchDtoUpdatedAt.md)> |  | [optional]
**deleted_at** | Option<[**models::BranchDtoUpdatedAt**](BranchDtoUpdatedAt.md)> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


