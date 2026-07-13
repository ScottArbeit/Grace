# CacheRepositoryAssignmentRequest


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**cache_id** | **UUID** |  | 
**repository_scopes** | [**List[CacheRepositoryScope]**](CacheRepositoryScope.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_repository_assignment_request import CacheRepositoryAssignmentRequest

# TODO update the JSON string below
json = "{}"
# create an instance of CacheRepositoryAssignmentRequest from a JSON string
cache_repository_assignment_request_instance = CacheRepositoryAssignmentRequest.from_json(json)
# print the JSON string representation of the object
print(CacheRepositoryAssignmentRequest.to_json())

# convert the object into a dict
cache_repository_assignment_request_dict = cache_repository_assignment_request_instance.to_dict()
# create an instance of CacheRepositoryAssignmentRequest from a dict
cache_repository_assignment_request_from_dict = CacheRepositoryAssignmentRequest.from_dict(cache_repository_assignment_request_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


