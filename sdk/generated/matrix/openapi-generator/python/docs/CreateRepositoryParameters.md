# CreateRepositoryParameters

Parameters for the /repository/create endpoint.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**object_storage_provider** | [**ObjectStorageProvider**](ObjectStorageProvider.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.create_repository_parameters import CreateRepositoryParameters

# TODO update the JSON string below
json = "{}"
# create an instance of CreateRepositoryParameters from a JSON string
create_repository_parameters_instance = CreateRepositoryParameters.from_json(json)
# print the JSON string representation of the object
print(CreateRepositoryParameters.to_json())

# convert the object into a dict
create_repository_parameters_dict = create_repository_parameters_instance.to_dict()
# create an instance of CreateRepositoryParameters from a dict
create_repository_parameters_from_dict = CreateRepositoryParameters.from_dict(create_repository_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


