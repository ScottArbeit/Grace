# DirectoryVersionReturnValue

Grace response envelope whose ReturnValue contains a directory version DTO.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**DirectoryVersionApiDto**](DirectoryVersionApiDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.directory_version_return_value import DirectoryVersionReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of DirectoryVersionReturnValue from a JSON string
directory_version_return_value_instance = DirectoryVersionReturnValue.from_json(json)
# print the JSON string representation of the object
print(DirectoryVersionReturnValue.to_json())

# convert the object into a dict
directory_version_return_value_dict = directory_version_return_value_instance.to_dict()
# create an instance of DirectoryVersionReturnValue from a dict
directory_version_return_value_from_dict = DirectoryVersionReturnValue.from_dict(directory_version_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


