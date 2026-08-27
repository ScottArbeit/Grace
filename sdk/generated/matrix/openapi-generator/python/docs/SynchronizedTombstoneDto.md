# SynchronizedTombstoneDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **UUID** |  | 
**item_kind** | [**SynchronizedItemKind**](SynchronizedItemKind.md) |  | 
**deleted_at** | **datetime** |  | 
**deleted_by** | **str** |  | 
**delete_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**last_namespace_version** | **UUID** |  | 
**last_content_version_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_tombstone_dto import SynchronizedTombstoneDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedTombstoneDto from a JSON string
synchronized_tombstone_dto_instance = SynchronizedTombstoneDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedTombstoneDto.to_json())

# convert the object into a dict
synchronized_tombstone_dto_dict = synchronized_tombstone_dto_instance.to_dict()
# create an instance of SynchronizedTombstoneDto from a dict
synchronized_tombstone_dto_from_dict = SynchronizedTombstoneDto.from_dict(synchronized_tombstone_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


