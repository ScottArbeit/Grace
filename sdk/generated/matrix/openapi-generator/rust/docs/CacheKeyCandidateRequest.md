# CacheKeyCandidateRequest

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**cache_id** | **uuid::Uuid** |  | 
**candidate_public_key** | [**models::CacheIdentityPublicKey**](CacheIdentityPublicKey.md) |  | 
**rotation_interval_minutes** | **i32** |  | [default to 240]
**is_startup** | **bool** |  | 
**proof** | [**models::SignedCacheRequestProof**](SignedCacheRequestProof.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


