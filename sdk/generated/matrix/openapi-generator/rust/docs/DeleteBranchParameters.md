# DeleteBranchParameters

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | Option<**String**> | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional]
**principal** | Option<**String**> | The entity on whose behalf the action is being performed. | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**owner_name** | Option<**String**> |  | [optional]
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_name** | Option<**String**> |  | [optional]
**repository_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_name** | Option<**String**> |  | [optional]
**branch_id** | Option<**uuid::Uuid**> |  | [optional]
**branch_name** | Option<**String**> |  | [optional]
**force** | Option<**bool**> |  | [optional]
**delete_reason** | Option<**String**> |  | [optional]
**reassign_child_branches** | Option<**bool**> |  | [optional]
**new_parent_branch_id** | Option<**uuid::Uuid**> |  | [optional]
**new_parent_branch_name** | Option<**String**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


