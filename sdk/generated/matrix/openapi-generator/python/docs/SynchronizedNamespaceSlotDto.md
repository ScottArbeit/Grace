# SynchronizedNamespaceSlotDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**parent** | [**SynchronizedParentDto**](SynchronizedParentDto.md) |  | 
**name** | **str** |  | 
**normalized_path** | **str** |  | 
**slot_version** | **UUID** |  | 
**state** | **str** |  | 
**occupant_item_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_namespace_slot_dto import SynchronizedNamespaceSlotDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedNamespaceSlotDto from a JSON string
synchronized_namespace_slot_dto_instance = SynchronizedNamespaceSlotDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedNamespaceSlotDto.to_json())

# convert the object into a dict
synchronized_namespace_slot_dto_dict = synchronized_namespace_slot_dto_instance.to_dict()
# create an instance of SynchronizedNamespaceSlotDto from a dict
synchronized_namespace_slot_dto_from_dict = SynchronizedNamespaceSlotDto.from_dict(synchronized_namespace_slot_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


