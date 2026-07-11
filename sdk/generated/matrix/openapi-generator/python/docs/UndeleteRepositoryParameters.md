# UndeleteRepositoryParameters

Parameters for the /repository/undelete endpoint.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**properties** | **Dict[str, str]** | Allow-listed event properties. UploadSessionIds is the only client-settable key. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.undelete_repository_parameters import UndeleteRepositoryParameters

# TODO update the JSON string below
json = "{}"
# create an instance of UndeleteRepositoryParameters from a JSON string
undelete_repository_parameters_instance = UndeleteRepositoryParameters.from_json(json)
# print the JSON string representation of the object
print(UndeleteRepositoryParameters.to_json())

# convert the object into a dict
undelete_repository_parameters_dict = undelete_repository_parameters_instance.to_dict()
# create an instance of UndeleteRepositoryParameters from a dict
undelete_repository_parameters_from_dict = UndeleteRepositoryParameters.from_dict(undelete_repository_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


