# UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 
**reminder_time** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_cleanup_reminder_scheduled_event_cleanup_reminder_scheduled import UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled from a JSON string
upload_session_cleanup_reminder_scheduled_event_cleanup_reminder_scheduled_instance = UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled.from_json(json)
# print the JSON string representation of the object
print(UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled.to_json())

# convert the object into a dict
upload_session_cleanup_reminder_scheduled_event_cleanup_reminder_scheduled_dict = upload_session_cleanup_reminder_scheduled_event_cleanup_reminder_scheduled_instance.to_dict()
# create an instance of UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled from a dict
upload_session_cleanup_reminder_scheduled_event_cleanup_reminder_scheduled_from_dict = UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled.from_dict(upload_session_cleanup_reminder_scheduled_event_cleanup_reminder_scheduled_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


