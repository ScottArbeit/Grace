# CacheRegistration

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**service_principal_id** | **String** |  | 
**endpoint** | **String** |  | 
**approved_scopes** | **Vec<String>** |  | 
**approved_capabilities** | **Vec<String>** |  | 
**approved_execution_modes** | [**Vec<models::MaterializationExecutionMode>**](MaterializationExecutionMode.md) |  | 
**registered_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**last_refreshed_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**refresh_after** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**expires_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**read_through_enabled** | **bool** |  | 
**prefetch_enabled** | **bool** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


