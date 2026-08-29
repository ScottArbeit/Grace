# LibraryContentReadGrantReturnValue


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 
**return_value** | [**LibraryContentReadGrantDto**](LibraryContentReadGrantDto.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.library_content_read_grant_return_value import LibraryContentReadGrantReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryContentReadGrantReturnValue from a JSON string
library_content_read_grant_return_value_instance = LibraryContentReadGrantReturnValue.from_json(json)
# print the JSON string representation of the object
print(LibraryContentReadGrantReturnValue.to_json())

# convert the object into a dict
library_content_read_grant_return_value_dict = library_content_read_grant_return_value_instance.to_dict()
# create an instance of LibraryContentReadGrantReturnValue from a dict
library_content_read_grant_return_value_from_dict = LibraryContentReadGrantReturnValue.from_dict(library_content_read_grant_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


