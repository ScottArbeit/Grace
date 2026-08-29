# LibraryReturnValueBase


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_return_value_base import LibraryReturnValueBase

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryReturnValueBase from a JSON string
library_return_value_base_instance = LibraryReturnValueBase.from_json(json)
# print the JSON string representation of the object
print(LibraryReturnValueBase.to_json())

# convert the object into a dict
library_return_value_base_dict = library_return_value_base_instance.to_dict()
# create an instance of LibraryReturnValueBase from a dict
library_return_value_base_from_dict = LibraryReturnValueBase.from_dict(library_return_value_base_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


