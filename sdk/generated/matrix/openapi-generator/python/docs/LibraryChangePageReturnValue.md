# LibraryChangePageReturnValue


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 
**return_value** | [**LibraryChangePageDto**](LibraryChangePageDto.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.library_change_page_return_value import LibraryChangePageReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryChangePageReturnValue from a JSON string
library_change_page_return_value_instance = LibraryChangePageReturnValue.from_json(json)
# print the JSON string representation of the object
print(LibraryChangePageReturnValue.to_json())

# convert the object into a dict
library_change_page_return_value_dict = library_change_page_return_value_instance.to_dict()
# create an instance of LibraryChangePageReturnValue from a dict
library_change_page_return_value_from_dict = LibraryChangePageReturnValue.from_dict(library_change_page_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


