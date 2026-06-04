# OwnerReturnValue

Grace response envelope whose ReturnValue contains an owner DTO.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**OwnerDto**](OwnerDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.owner_return_value import OwnerReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of OwnerReturnValue from a JSON string
owner_return_value_instance = OwnerReturnValue.from_json(json)
# print the JSON string representation of the object
print(OwnerReturnValue.to_json())

# convert the object into a dict
owner_return_value_dict = owner_return_value_instance.to_dict()
# create an instance of OwnerReturnValue from a dict
owner_return_value_from_dict = OwnerReturnValue.from_dict(owner_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


