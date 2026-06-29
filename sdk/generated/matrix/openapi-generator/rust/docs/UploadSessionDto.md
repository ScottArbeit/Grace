# UploadSessionDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **Class** |  (enum: UploadSessionDto) | 
**upload_session_id** | **uuid::Uuid** |  | 
**owner_id** | **uuid::Uuid** |  | 
**organization_id** | **uuid::Uuid** |  | 
**repository_id** | **uuid::Uuid** |  | 
**storage_pool_id** | **String** | StoragePool-wide CAS scope identifier. Public clients treat this as server-provided placement evidence and must not use it to select storage accounts, containers, buckets, prefixes, or write authority directly. | 
**authorized_scope** | **String** |  | 
**file_content_hash** | **String** | Lowercase 64-character BLAKE3 hash of the complete unencoded file bytes. | 
**expected_size** | **i64** |  | 
**chunking_suite_id** | **String** | Versioned chunking suite identifier. | 
**sampling_policy_snapshot** | **String** |  | 
**lifecycle_state** | [**models::UploadSessionLifecycleState**](UploadSessionLifecycleState.md) |  | 
**started_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**completed_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**finalized_manifest_address** | **String** | Lowercase 64-character BLAKE3-derived FileManifest address. | 
**block_upload_intents** | [**Vec<models::BlockUploadIntent>**](BlockUploadIntent.md) |  | 
**confirmed_block_uploads** | [**Vec<models::ConfirmedBlockUpload>**](ConfirmedBlockUpload.md) |  | 
**dedupe_discovery** | [**models::DedupeDiscoverySnapshot**](DedupeDiscoverySnapshot.md) |  | 
**claimed_reuse_ranges** | [**Vec<models::ClaimedReuseRange>**](ClaimedReuseRange.md) |  | 
**cleanup_reminder_scheduled_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**cleanup_reminder_operation_id** | **String** | Caller-supplied idempotency key for one upload-session operation. | 
**last_operation_id** | **String** | Caller-supplied idempotency key for one upload-session operation. | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


