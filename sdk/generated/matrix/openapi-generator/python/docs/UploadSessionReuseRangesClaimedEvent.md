# UploadSessionReuseRangesClaimedEvent


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**reuse_ranges_claimed** | [**UploadSessionReuseRangesClaimedEventReuseRangesClaimed**](UploadSessionReuseRangesClaimedEventReuseRangesClaimed.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_reuse_ranges_claimed_event import UploadSessionReuseRangesClaimedEvent

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionReuseRangesClaimedEvent from a JSON string
upload_session_reuse_ranges_claimed_event_instance = UploadSessionReuseRangesClaimedEvent.from_json(json)
# print the JSON string representation of the object
print(UploadSessionReuseRangesClaimedEvent.to_json())

# convert the object into a dict
upload_session_reuse_ranges_claimed_event_dict = upload_session_reuse_ranges_claimed_event_instance.to_dict()
# create an instance of UploadSessionReuseRangesClaimedEvent from a dict
upload_session_reuse_ranges_claimed_event_from_dict = UploadSessionReuseRangesClaimedEvent.from_dict(upload_session_reuse_ranges_claimed_event_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


