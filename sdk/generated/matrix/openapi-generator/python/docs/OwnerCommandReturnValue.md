# OwnerCommandReturnValue

Grace response envelope returned by owner command endpoints.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | **str** |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.owner_command_return_value import OwnerCommandReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of OwnerCommandReturnValue from a JSON string
owner_command_return_value_instance = OwnerCommandReturnValue.from_json(json)
# print the JSON string representation of the object
print(OwnerCommandReturnValue.to_json())

# convert the object into a dict
owner_command_return_value_dict = owner_command_return_value_instance.to_dict()
# create an instance of OwnerCommandReturnValue from a dict
owner_command_return_value_from_dict = OwnerCommandReturnValue.from_dict(owner_command_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


