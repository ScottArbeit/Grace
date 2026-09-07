# LibraryContentPreparationReturnValue


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 
**return_value** | [**LibraryContentPreparationDto**](LibraryContentPreparationDto.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.library_content_preparation_return_value import LibraryContentPreparationReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryContentPreparationReturnValue from a JSON string
library_content_preparation_return_value_instance = LibraryContentPreparationReturnValue.from_json(json)
# print the JSON string representation of the object
print(LibraryContentPreparationReturnValue.to_json())

# convert the object into a dict
library_content_preparation_return_value_dict = library_content_preparation_return_value_instance.to_dict()
# create an instance of LibraryContentPreparationReturnValue from a dict
library_content_preparation_return_value_from_dict = LibraryContentPreparationReturnValue.from_dict(library_content_preparation_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


