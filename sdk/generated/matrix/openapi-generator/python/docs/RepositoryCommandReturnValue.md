# RepositoryCommandReturnValue

Grace response envelope returned by repository command endpoints.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | **str** |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.repository_command_return_value import RepositoryCommandReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of RepositoryCommandReturnValue from a JSON string
repository_command_return_value_instance = RepositoryCommandReturnValue.from_json(json)
# print the JSON string representation of the object
print(RepositoryCommandReturnValue.to_json())

# convert the object into a dict
repository_command_return_value_dict = repository_command_return_value_instance.to_dict()
# create an instance of RepositoryCommandReturnValue from a dict
repository_command_return_value_from_dict = RepositoryCommandReturnValue.from_dict(repository_command_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


