# SynchronizedRepositoryStatusDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**state** | **String** |  | 
**repository_id** | **uuid::Uuid** |  | 
**root_configuration_version** | **uuid::Uuid** |  | 
**is_caught_up** | **bool** |  | 
**rebaseline_required** | **bool** |  | 
**is_blocked** | **bool** |  | 
**pending_operation_count** | **i32** |  | 
**oldest_pending_age_milliseconds** | **i64** |  | 
**projection_lag_count** | **i64** |  | 
**last_completed_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


