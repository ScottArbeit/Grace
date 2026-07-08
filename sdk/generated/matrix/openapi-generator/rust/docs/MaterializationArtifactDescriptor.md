# MaterializationArtifactDescriptor

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**artifact_kind** | [**models::MaterializationArtifactKind**](MaterializationArtifactKind.md) |  | 
**canonical_artifact_identity** | Option<**String**> |  | [optional]
**represented_root_directory_version_id** | Option<**uuid::Uuid**> |  | [optional]
**target_root_directory_version_id** | **uuid::Uuid** |  | 
**size_in_bytes** | Option<**i64**> |  | [optional]
**relative_path** | Option<**String**> |  | [optional]
**sha256_hash** | Option<**String**> | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | [optional]
**blake3_hash** | Option<**String**> | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | [optional]
**manifest_address** | Option<**String**> | Lowercase 64-character BLAKE3-derived FileManifest address. | [optional]
**content_block_address** | Option<**String**> | Lowercase 64-character BLAKE3-derived ContentBlock address. | [optional]
**storage_pool_id** | Option<**String**> | StoragePool-wide CAS scope identifier. Public clients treat this as server-provided placement evidence and must not use it to select storage accounts, containers, buckets, prefixes, or write authority directly. | [optional]
**source** | Option<[**models::MaterializationArtifactSource**](MaterializationArtifactSource.md)> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


