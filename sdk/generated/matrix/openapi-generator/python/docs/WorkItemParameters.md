# WorkItemParameters


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

## Example

```python
from grace_generated_openapi_probe.models.work_item_parameters import WorkItemParameters

# TODO update the JSON string below
json = "{}"
# create an instance of WorkItemParameters from a JSON string
work_item_parameters_instance = WorkItemParameters.from_json(json)
# print the JSON string representation of the object
print(WorkItemParameters.to_json())

# convert the object into a dict
work_item_parameters_dict = work_item_parameters_instance.to_dict()
# create an instance of WorkItemParameters from a dict
work_item_parameters_from_dict = WorkItemParameters.from_dict(work_item_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


