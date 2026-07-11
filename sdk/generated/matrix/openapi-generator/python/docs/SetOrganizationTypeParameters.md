# SetOrganizationTypeParameters

Parameters for the /organization/setType endpoint.

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
**organization_type** | [**OrganizationType**](OrganizationType.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.set_organization_type_parameters import SetOrganizationTypeParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SetOrganizationTypeParameters from a JSON string
set_organization_type_parameters_instance = SetOrganizationTypeParameters.from_json(json)
# print the JSON string representation of the object
print(SetOrganizationTypeParameters.to_json())

# convert the object into a dict
set_organization_type_parameters_dict = set_organization_type_parameters_instance.to_dict()
# create an instance of SetOrganizationTypeParameters from a dict
set_organization_type_parameters_from_dict = SetOrganizationTypeParameters.from_dict(set_organization_type_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


