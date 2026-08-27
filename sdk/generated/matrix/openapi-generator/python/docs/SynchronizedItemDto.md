# SynchronizedItemDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **UUID** |  | 
**item_kind** | [**SynchronizedItemKind**](SynchronizedItemKind.md) |  | 
**state** | **str** |  | 
**last_mutation_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**root_configuration_version** | **UUID** |  | 
**namespace** | [**SynchronizedNamespaceDto**](SynchronizedNamespaceDto.md) |  | 
**content** | [**SynchronizedContentVersionDto**](SynchronizedContentVersionDto.md) |  | 
**tombstone** | [**SynchronizedTombstoneDto**](SynchronizedTombstoneDto.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_item_dto import SynchronizedItemDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedItemDto from a JSON string
synchronized_item_dto_instance = SynchronizedItemDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedItemDto.to_json())

# convert the object into a dict
synchronized_item_dto_dict = synchronized_item_dto_instance.to_dict()
# create an instance of SynchronizedItemDto from a dict
synchronized_item_dto_from_dict = SynchronizedItemDto.from_dict(synchronized_item_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


