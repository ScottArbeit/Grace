# SynchronizedContentPreconditionDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **UUID** |  | 
**expected_content_version_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_content_precondition_dto import SynchronizedContentPreconditionDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedContentPreconditionDto from a JSON string
synchronized_content_precondition_dto_instance = SynchronizedContentPreconditionDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedContentPreconditionDto.to_json())

# convert the object into a dict
synchronized_content_precondition_dto_dict = synchronized_content_precondition_dto_instance.to_dict()
# create an instance of SynchronizedContentPreconditionDto from a dict
synchronized_content_precondition_dto_from_dict = SynchronizedContentPreconditionDto.from_dict(synchronized_content_precondition_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


