# GraceReturnValue


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | **object** |  | [optional] 
**event_time** | **datetime** |  | [optional] 
**correlation_id** | **str** |  | [optional] 
**properties** | **Dict[str, str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.grace_return_value import GraceReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of GraceReturnValue from a JSON string
grace_return_value_instance = GraceReturnValue.from_json(json)
# print the JSON string representation of the object
print(GraceReturnValue.to_json())

# convert the object into a dict
grace_return_value_dict = grace_return_value_instance.to_dict()
# create an instance of GraceReturnValue from a dict
grace_return_value_from_dict = GraceReturnValue.from_dict(grace_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


