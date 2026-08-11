# SetWorkItemDescriptionParameters


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
**work_item_id** | **str** | Work-item GUID or positive repository-scoped number. | 
**text** | **str** | The complete replacement description as UTF-8 text. | 

## Example

```python
from grace_generated_openapi_probe.models.set_work_item_description_parameters import SetWorkItemDescriptionParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SetWorkItemDescriptionParameters from a JSON string
set_work_item_description_parameters_instance = SetWorkItemDescriptionParameters.from_json(json)
# print the JSON string representation of the object
print(SetWorkItemDescriptionParameters.to_json())

# convert the object into a dict
set_work_item_description_parameters_dict = set_work_item_description_parameters_instance.to_dict()
# create an instance of SetWorkItemDescriptionParameters from a dict
set_work_item_description_parameters_from_dict = SetWorkItemDescriptionParameters.from_dict(set_work_item_description_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


