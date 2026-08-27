# SynchronizedNamespaceDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**parent** | [**SynchronizedParentDto**](SynchronizedParentDto.md) |  | 
**name** | **str** |  | 
**normalized_path** | **str** |  | 
**namespace_version** | **UUID** |  | 
**slot_version** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_namespace_dto import SynchronizedNamespaceDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedNamespaceDto from a JSON string
synchronized_namespace_dto_instance = SynchronizedNamespaceDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedNamespaceDto.to_json())

# convert the object into a dict
synchronized_namespace_dto_dict = synchronized_namespace_dto_instance.to_dict()
# create an instance of SynchronizedNamespaceDto from a dict
synchronized_namespace_dto_from_dict = SynchronizedNamespaceDto.from_dict(synchronized_namespace_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


