# CacheRegistrationRefreshRequest

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**cache_id** | **uuid::Uuid** |  | 
**endpoint** | **String** | Absolute HTTP(S) Cache origin with path '/', no user info, query, or fragment. | 
**health** | [**models::CacheHealthStatus**](CacheHealthStatus.md) |  | 
**software_version** | **String** |  | 
**protocol_version** | **String** |  | 
**prefetch_supported** | **bool** |  | 
**observed_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**proof** | [**models::SignedCacheRequestProof**](SignedCacheRequestProof.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


