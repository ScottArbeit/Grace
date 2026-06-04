# UndeleteOwnerParameters

Parameters for the /owner/undelete endpoint.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.undelete_owner_parameters import UndeleteOwnerParameters

# TODO update the JSON string below
json = "{}"
# create an instance of UndeleteOwnerParameters from a JSON string
undelete_owner_parameters_instance = UndeleteOwnerParameters.from_json(json)
# print the JSON string representation of the object
print(UndeleteOwnerParameters.to_json())

# convert the object into a dict
undelete_owner_parameters_dict = undelete_owner_parameters_instance.to_dict()
# create an instance of UndeleteOwnerParameters from a dict
undelete_owner_parameters_from_dict = UndeleteOwnerParameters.from_dict(undelete_owner_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


