# GetSynchronizedDeltasParameters


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
**after_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**page_token** | **str** | Opaque token for continuing one immutable page sequence. | 
**page_size** | **int** |  | 

## Example

```python
from grace_generated_openapi_probe.models.get_synchronized_deltas_parameters import GetSynchronizedDeltasParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetSynchronizedDeltasParameters from a JSON string
get_synchronized_deltas_parameters_instance = GetSynchronizedDeltasParameters.from_json(json)
# print the JSON string representation of the object
print(GetSynchronizedDeltasParameters.to_json())

# convert the object into a dict
get_synchronized_deltas_parameters_dict = get_synchronized_deltas_parameters_instance.to_dict()
# create an instance of GetSynchronizedDeltasParameters from a dict
get_synchronized_deltas_parameters_from_dict = GetSynchronizedDeltasParameters.from_dict(get_synchronized_deltas_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


