# FileManifest

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **Class** |  (enum: FileManifest) | 
**manifest_address** | **String** | Lowercase 64-character BLAKE3-derived FileManifest address. | 
**chunking_suite_id** | **String** | Versioned chunking suite identifier. | 
**file_content_hash** | **String** | Lowercase 64-character BLAKE3 hash of the complete unencoded file bytes. | 
**storage_pool_id** | **String** | StoragePool-wide CAS scope identifier. Public clients treat this as server-provided placement evidence and must not use it to select storage accounts, containers, buckets, prefixes, or write authority directly. | 
**size** | **i64** |  | 
**blocks** | [**Vec<models::ContentBlock>**](ContentBlock.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


