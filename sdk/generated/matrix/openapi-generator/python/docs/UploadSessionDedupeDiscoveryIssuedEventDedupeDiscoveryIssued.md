# UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 
**discovery** | [**DedupeDiscoverySnapshot**](DedupeDiscoverySnapshot.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_dedupe_discovery_issued_event_dedupe_discovery_issued import UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued from a JSON string
upload_session_dedupe_discovery_issued_event_dedupe_discovery_issued_instance = UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued.from_json(json)
# print the JSON string representation of the object
print(UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued.to_json())

# convert the object into a dict
upload_session_dedupe_discovery_issued_event_dedupe_discovery_issued_dict = upload_session_dedupe_discovery_issued_event_dedupe_discovery_issued_instance.to_dict()
# create an instance of UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued from a dict
upload_session_dedupe_discovery_issued_event_dedupe_discovery_issued_from_dict = UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued.from_dict(upload_session_dedupe_discovery_issued_event_dedupe_discovery_issued_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


