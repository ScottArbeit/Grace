# DirectoryVersionListReturnValue

Grace response envelope whose ReturnValue contains directory version DTOs.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**List[DirectoryVersionApiDto]**](DirectoryVersionApiDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.directory_version_list_return_value import DirectoryVersionListReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of DirectoryVersionListReturnValue from a JSON string
directory_version_list_return_value_instance = DirectoryVersionListReturnValue.from_json(json)
# print the JSON string representation of the object
print(DirectoryVersionListReturnValue.to_json())

# convert the object into a dict
directory_version_list_return_value_dict = directory_version_list_return_value_instance.to_dict()
# create an instance of DirectoryVersionListReturnValue from a dict
directory_version_list_return_value_from_dict = DirectoryVersionListReturnValue.from_dict(directory_version_list_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


