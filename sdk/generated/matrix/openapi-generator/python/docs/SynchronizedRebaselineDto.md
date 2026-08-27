# SynchronizedRebaselineDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**reason** | **str** |  | 
**current_epoch** | **str** | Opaque repository epoch. Clients compare only exact equality. | 
**service_floor_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**recommended_bootstrap** | **bool** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_rebaseline_dto import SynchronizedRebaselineDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedRebaselineDto from a JSON string
synchronized_rebaseline_dto_instance = SynchronizedRebaselineDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedRebaselineDto.to_json())

# convert the object into a dict
synchronized_rebaseline_dto_dict = synchronized_rebaseline_dto_instance.to_dict()
# create an instance of SynchronizedRebaselineDto from a dict
synchronized_rebaseline_dto_from_dict = SynchronizedRebaselineDto.from_dict(synchronized_rebaseline_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


