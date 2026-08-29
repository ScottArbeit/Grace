# ListLibrariesParameters


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

## Example

```python
from grace_generated_openapi_probe.models.list_libraries_parameters import ListLibrariesParameters

# TODO update the JSON string below
json = "{}"
# create an instance of ListLibrariesParameters from a JSON string
list_libraries_parameters_instance = ListLibrariesParameters.from_json(json)
# print the JSON string representation of the object
print(ListLibrariesParameters.to_json())

# convert the object into a dict
list_libraries_parameters_dict = list_libraries_parameters_instance.to_dict()
# create an instance of ListLibrariesParameters from a dict
list_libraries_parameters_from_dict = ListLibrariesParameters.from_dict(list_libraries_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


