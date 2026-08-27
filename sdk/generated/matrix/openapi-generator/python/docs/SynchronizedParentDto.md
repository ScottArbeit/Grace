# SynchronizedParentDto

Repository-owned root or directory parent identity.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**kind** | **str** |  | 
**root_path** | **str** |  | 
**item_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_parent_dto import SynchronizedParentDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedParentDto from a JSON string
synchronized_parent_dto_instance = SynchronizedParentDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedParentDto.to_json())

# convert the object into a dict
synchronized_parent_dto_dict = synchronized_parent_dto_instance.to_dict()
# create an instance of SynchronizedParentDto from a dict
synchronized_parent_dto_from_dict = SynchronizedParentDto.from_dict(synchronized_parent_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


