# SynchronizedBootstrapPageReturnValue


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 
**return_value** | [**SynchronizedBootstrapPageDto**](SynchronizedBootstrapPageDto.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_bootstrap_page_return_value import SynchronizedBootstrapPageReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedBootstrapPageReturnValue from a JSON string
synchronized_bootstrap_page_return_value_instance = SynchronizedBootstrapPageReturnValue.from_json(json)
# print the JSON string representation of the object
print(SynchronizedBootstrapPageReturnValue.to_json())

# convert the object into a dict
synchronized_bootstrap_page_return_value_dict = synchronized_bootstrap_page_return_value_instance.to_dict()
# create an instance of SynchronizedBootstrapPageReturnValue from a dict
synchronized_bootstrap_page_return_value_from_dict = SynchronizedBootstrapPageReturnValue.from_dict(synchronized_bootstrap_page_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


