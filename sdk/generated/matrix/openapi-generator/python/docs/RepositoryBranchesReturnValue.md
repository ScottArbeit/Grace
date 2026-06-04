# RepositoryBranchesReturnValue

Grace response envelope whose ReturnValue contains branch DTOs.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**List[BranchDto]**](BranchDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.repository_branches_return_value import RepositoryBranchesReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of RepositoryBranchesReturnValue from a JSON string
repository_branches_return_value_instance = RepositoryBranchesReturnValue.from_json(json)
# print the JSON string representation of the object
print(RepositoryBranchesReturnValue.to_json())

# convert the object into a dict
repository_branches_return_value_dict = repository_branches_return_value_instance.to_dict()
# create an instance of RepositoryBranchesReturnValue from a dict
repository_branches_return_value_from_dict = RepositoryBranchesReturnValue.from_dict(repository_branches_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


