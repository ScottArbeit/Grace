# UploadSessionReuseRangesClaimedEventReuseRangesClaimed


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 
**claimed_ranges** | [**List[ClaimedReuseRange]**](ClaimedReuseRange.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_reuse_ranges_claimed_event_reuse_ranges_claimed import UploadSessionReuseRangesClaimedEventReuseRangesClaimed

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionReuseRangesClaimedEventReuseRangesClaimed from a JSON string
upload_session_reuse_ranges_claimed_event_reuse_ranges_claimed_instance = UploadSessionReuseRangesClaimedEventReuseRangesClaimed.from_json(json)
# print the JSON string representation of the object
print(UploadSessionReuseRangesClaimedEventReuseRangesClaimed.to_json())

# convert the object into a dict
upload_session_reuse_ranges_claimed_event_reuse_ranges_claimed_dict = upload_session_reuse_ranges_claimed_event_reuse_ranges_claimed_instance.to_dict()
# create an instance of UploadSessionReuseRangesClaimedEventReuseRangesClaimed from a dict
upload_session_reuse_ranges_claimed_event_reuse_ranges_claimed_from_dict = UploadSessionReuseRangesClaimedEventReuseRangesClaimed.from_dict(upload_session_reuse_ranges_claimed_event_reuse_ranges_claimed_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


