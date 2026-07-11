# CreateOwnerParameters

Parameters for the /owner/create endpoint.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**properties** | **Dict[str, str]** | Allow-listed event properties. UploadSessionIds is the only client-settable key. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.create_owner_parameters import CreateOwnerParameters

# TODO update the JSON string below
json = "{}"
# create an instance of CreateOwnerParameters from a JSON string
create_owner_parameters_instance = CreateOwnerParameters.from_json(json)
# print the JSON string representation of the object
print(CreateOwnerParameters.to_json())

# convert the object into a dict
create_owner_parameters_dict = create_owner_parameters_instance.to_dict()
# create an instance of CreateOwnerParameters from a dict
create_owner_parameters_from_dict = CreateOwnerParameters.from_dict(create_owner_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


