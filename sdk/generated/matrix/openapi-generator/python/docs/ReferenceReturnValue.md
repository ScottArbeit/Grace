# ReferenceReturnValue

Grace response envelope whose ReturnValue contains a reference.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.reference_return_value import ReferenceReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of ReferenceReturnValue from a JSON string
reference_return_value_instance = ReferenceReturnValue.from_json(json)
# print the JSON string representation of the object
print(ReferenceReturnValue.to_json())

# convert the object into a dict
reference_return_value_dict = reference_return_value_instance.to_dict()
# create an instance of ReferenceReturnValue from a dict
reference_return_value_from_dict = ReferenceReturnValue.from_dict(reference_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


