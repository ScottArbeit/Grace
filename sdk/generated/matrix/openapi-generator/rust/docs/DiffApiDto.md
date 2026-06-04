# DiffApiDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_id** | Option<**uuid::Uuid**> |  | [optional]
**directory_version_id1** | Option<**uuid::Uuid**> |  | [optional]
**directory1_created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**directory_version_id2** | Option<**uuid::Uuid**> |  | [optional]
**directory2_created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**has_differences** | Option<**bool**> |  | [optional]
**differences** | Option<[**Vec<models::FileSystemDifference>**](FileSystemDifference.md)> |  | [optional]
**file_diffs** | Option<[**Vec<models::FileDiff>**](FileDiff.md)> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


