# UploadSessionDto

Server state snapshot for a manifest upload session.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**upload_session_id** | **UUID** |  | 
**owner_id** | **UUID** |  | 
**organization_id** | **UUID** |  | 
**repository_id** | **UUID** |  | 
**authorized_scope** | **str** |  | 
**file_content_hash** | **str** | Lowercase 64-character BLAKE3 hash of the complete unencoded file bytes. | 
**expected_size** | **int** |  | 
**chunking_suite_id** | **str** | Versioned chunking suite identifier. | 
**sampling_policy_snapshot** | **str** |  | 
**lifecycle_state** | [**UploadSessionLifecycleState**](UploadSessionLifecycleState.md) |  | 
**started_at** | **datetime** |  | 
**completed_at** | **datetime** |  | 
**finalized_manifest_address** | **str** | Lowercase 64-character BLAKE3-derived FileManifest address. | 
**block_upload_intents** | [**List[BlockUploadIntent]**](BlockUploadIntent.md) |  | 
**confirmed_block_uploads** | [**List[ConfirmedBlockUpload]**](ConfirmedBlockUpload.md) |  | 
**dedupe_discovery** | [**DedupeDiscoverySnapshot**](DedupeDiscoverySnapshot.md) |  | 
**claimed_reuse_ranges** | [**List[ClaimedReuseRange]**](ClaimedReuseRange.md) |  | 
**cleanup_reminder_scheduled_at** | **datetime** |  | 
**cleanup_reminder_operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 
**last_operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_dto import UploadSessionDto

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionDto from a JSON string
upload_session_dto_instance = UploadSessionDto.from_json(json)
# print the JSON string representation of the object
print(UploadSessionDto.to_json())

# convert the object into a dict
upload_session_dto_dict = upload_session_dto_instance.to_dict()
# create an instance of UploadSessionDto from a dict
upload_session_dto_from_dict = UploadSessionDto.from_dict(upload_session_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


