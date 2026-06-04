# RepositoryReferencesReturnValue

Grace response envelope whose ReturnValue contains reference DTOs.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**List[ReferenceDto]**](ReferenceDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.repository_references_return_value import RepositoryReferencesReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of RepositoryReferencesReturnValue from a JSON string
repository_references_return_value_instance = RepositoryReferencesReturnValue.from_json(json)
# print the JSON string representation of the object
print(RepositoryReferencesReturnValue.to_json())

# convert the object into a dict
repository_references_return_value_dict = repository_references_return_value_instance.to_dict()
# create an instance of RepositoryReferencesReturnValue from a dict
repository_references_return_value_from_dict = RepositoryReferencesReturnValue.from_dict(repository_references_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


