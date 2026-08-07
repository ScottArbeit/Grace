# CurrentBranchReferenceNotification

Root identity for one eligible current-branch Reference event.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**reference_id** | **UUID** |  | 
**owner_id** | **UUID** |  | 
**organization_id** | **UUID** |  | 
**repository_id** | **UUID** |  | 
**branch_id** | **UUID** |  | 
**branch_name** | **str** |  | 
**directory_id** | **UUID** |  | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**reference_type** | [**ReferenceType**](ReferenceType.md) |  | 
**reference_text** | **str** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 

## Example

```python
from grace_generated_openapi_probe.models.current_branch_reference_notification import CurrentBranchReferenceNotification

# TODO update the JSON string below
json = "{}"
# create an instance of CurrentBranchReferenceNotification from a JSON string
current_branch_reference_notification_instance = CurrentBranchReferenceNotification.from_json(json)
# print the JSON string representation of the object
print(CurrentBranchReferenceNotification.to_json())

# convert the object into a dict
current_branch_reference_notification_dict = current_branch_reference_notification_instance.to_dict()
# create an instance of CurrentBranchReferenceNotification from a dict
current_branch_reference_notification_from_dict = CurrentBranchReferenceNotification.from_dict(current_branch_reference_notification_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


