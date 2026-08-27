# ContinueSynchronizedBootstrapParameters


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
**bootstrap_id** | **UUID** |  | 
**page_token** | **str** | Opaque token for continuing one immutable page sequence. | 
**page_size** | **int** |  | 

## Example

```python
from grace_generated_openapi_probe.models.continue_synchronized_bootstrap_parameters import ContinueSynchronizedBootstrapParameters

# TODO update the JSON string below
json = "{}"
# create an instance of ContinueSynchronizedBootstrapParameters from a JSON string
continue_synchronized_bootstrap_parameters_instance = ContinueSynchronizedBootstrapParameters.from_json(json)
# print the JSON string representation of the object
print(ContinueSynchronizedBootstrapParameters.to_json())

# convert the object into a dict
continue_synchronized_bootstrap_parameters_dict = continue_synchronized_bootstrap_parameters_instance.to_dict()
# create an instance of ContinueSynchronizedBootstrapParameters from a dict
continue_synchronized_bootstrap_parameters_from_dict = ContinueSynchronizedBootstrapParameters.from_dict(continue_synchronized_bootstrap_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


