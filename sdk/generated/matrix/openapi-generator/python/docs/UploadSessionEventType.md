# UploadSessionEventType

Upload-session event union payload. Event objects contain the fields for one emitted union case.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**started** | [**StartUploadSession**](StartUploadSession.md) |  | 
**event_case** | **str** |  | 
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 
**finalized** | [**UploadSessionFinalizedEventFinalized**](UploadSessionFinalizedEventFinalized.md) |  | 
**cleanup_reminder_scheduled** | [**UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled**](UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled.md) |  | 
**block_upload_intent_registered** | [**UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered**](UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered.md) |  | 
**block_upload_confirmed** | [**UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed**](UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed.md) |  | 
**dedupe_discovery_issued** | [**UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued**](UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued.md) |  | 
**reuse_ranges_claimed** | [**UploadSessionReuseRangesClaimedEventReuseRangesClaimed**](UploadSessionReuseRangesClaimedEventReuseRangesClaimed.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_event_type import UploadSessionEventType

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionEventType from a JSON string
upload_session_event_type_instance = UploadSessionEventType.from_json(json)
# print the JSON string representation of the object
print(UploadSessionEventType.to_json())

# convert the object into a dict
upload_session_event_type_dict = upload_session_event_type_instance.to_dict()
# create an instance of UploadSessionEventType from a dict
upload_session_event_type_from_dict = UploadSessionEventType.from_dict(upload_session_event_type_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


