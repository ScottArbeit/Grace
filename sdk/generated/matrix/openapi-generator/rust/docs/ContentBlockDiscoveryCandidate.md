# ContentBlockDiscoveryCandidate

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**storage_pool_id** | Option<**String**> | StoragePool-wide CAS scope identifier. Public clients treat this as server-provided placement evidence and must not use it to select storage accounts, containers, buckets, prefixes, or write authority directly. | [optional]
**manifest_address** | Option<**String**> | Lowercase 64-character BLAKE3-derived FileManifest address. | [optional]
**content_block_address** | Option<**String**> | Lowercase 64-character BLAKE3-derived ContentBlock address. | [optional]
**ordinal_start** | Option<**i32**> |  | [optional]
**ordinal_count** | Option<**i32**> |  | [optional]
**metadata_version** | Option<**i64**> |  | [optional]
**matching_key_chunk_count** | Option<**i32**> |  | [optional]
**protected_chunk_addresses** | Option<**Vec<String>**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


