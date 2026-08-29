# LibraryBootstrapPageReturnValue


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 
**return_value** | [**LibraryBootstrapPageDto**](LibraryBootstrapPageDto.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.library_bootstrap_page_return_value import LibraryBootstrapPageReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryBootstrapPageReturnValue from a JSON string
library_bootstrap_page_return_value_instance = LibraryBootstrapPageReturnValue.from_json(json)
# print the JSON string representation of the object
print(LibraryBootstrapPageReturnValue.to_json())

# convert the object into a dict
library_bootstrap_page_return_value_dict = library_bootstrap_page_return_value_instance.to_dict()
# create an instance of LibraryBootstrapPageReturnValue from a dict
library_bootstrap_page_return_value_from_dict = LibraryBootstrapPageReturnValue.from_dict(library_bootstrap_page_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


