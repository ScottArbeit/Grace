# CacheRegistrationRequest

Request body used by an approved Grace Cache service to register its endpoint and requested boundary.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**endpoint** | **str** |  | 
**requested_scopes** | **List[str]** | Materialization-plan selection requires stable &#x60;repository:&lt;OwnerId&gt;/&lt;OrganizationId&gt;/&lt;RepositoryId&gt;&#x60; scopes; repository names and &#x60;storage-pool:*&#x60; scopes do not match, and multi-repository Cache registrations list each repository scope explicitly. | 
**requested_capabilities** | **List[str]** |  | 
**requested_execution_modes** | [**List[MaterializationExecutionMode]**](MaterializationExecutionMode.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_registration_request import CacheRegistrationRequest

# TODO update the JSON string below
json = "{}"
# create an instance of CacheRegistrationRequest from a JSON string
cache_registration_request_instance = CacheRegistrationRequest.from_json(json)
# print the JSON string representation of the object
print(CacheRegistrationRequest.to_json())

# convert the object into a dict
cache_registration_request_dict = cache_registration_request_instance.to_dict()
# create an instance of CacheRegistrationRequest from a dict
cache_registration_request_from_dict = CacheRegistrationRequest.from_dict(cache_registration_request_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


