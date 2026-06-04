# DedupeDiscoverySnapshot

Server-issued dedupe discovery snapshot retained by an upload session.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 
**expires_at** | **datetime** |  | 
**minimum_reuse_run_length** | **int** |  | 
**hints** | [**List[ContentBlockReuseRangeHint]**](ContentBlockReuseRangeHint.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.dedupe_discovery_snapshot import DedupeDiscoverySnapshot

# TODO update the JSON string below
json = "{}"
# create an instance of DedupeDiscoverySnapshot from a JSON string
dedupe_discovery_snapshot_instance = DedupeDiscoverySnapshot.from_json(json)
# print the JSON string representation of the object
print(DedupeDiscoverySnapshot.to_json())

# convert the object into a dict
dedupe_discovery_snapshot_dict = dedupe_discovery_snapshot_instance.to_dict()
# create an instance of DedupeDiscoverySnapshot from a dict
dedupe_discovery_snapshot_from_dict = DedupeDiscoverySnapshot.from_dict(dedupe_discovery_snapshot_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


