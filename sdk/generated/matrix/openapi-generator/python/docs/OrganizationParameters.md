# OrganizationParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.organization_parameters import OrganizationParameters

# TODO update the JSON string below
json = "{}"
# create an instance of OrganizationParameters from a JSON string
organization_parameters_instance = OrganizationParameters.from_json(json)
# print the JSON string representation of the object
print(OrganizationParameters.to_json())

# convert the object into a dict
organization_parameters_dict = organization_parameters_instance.to_dict()
# create an instance of OrganizationParameters from a dict
organization_parameters_from_dict = OrganizationParameters.from_dict(organization_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


