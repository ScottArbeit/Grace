# ClaimedReuseRange

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**storage_pool_id** | **String** | StoragePool-wide CAS scope identifier. Public clients treat this as server-provided placement evidence and must not use it to select storage accounts, containers, buckets, prefixes, or write authorization directly. | 
**content_block_address** | **String** | Lowercase 64-character BLAKE3-derived ContentBlock address. | 
**ordinal_start** | **i32** |  | 
**ordinal_count** | **i32** |  | 
**physical_offset** | **i64** |  | 
**physical_length** | **i64** |  | 
**metadata_version** | **i64** |  | 
**claimed_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


