# SynchronizedNamespacePreconditionDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **UUID** |  | 
**expected_namespace_version** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_namespace_precondition_dto import SynchronizedNamespacePreconditionDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedNamespacePreconditionDto from a JSON string
synchronized_namespace_precondition_dto_instance = SynchronizedNamespacePreconditionDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedNamespacePreconditionDto.to_json())

# convert the object into a dict
synchronized_namespace_precondition_dto_dict = synchronized_namespace_precondition_dto_instance.to_dict()
# create an instance of SynchronizedNamespacePreconditionDto from a dict
synchronized_namespace_precondition_dto_from_dict = SynchronizedNamespacePreconditionDto.from_dict(synchronized_namespace_precondition_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


