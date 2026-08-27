# SynchronizedContentReadGrantReturnValue


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 
**return_value** | [**SynchronizedContentReadGrantDto**](SynchronizedContentReadGrantDto.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_content_read_grant_return_value import SynchronizedContentReadGrantReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedContentReadGrantReturnValue from a JSON string
synchronized_content_read_grant_return_value_instance = SynchronizedContentReadGrantReturnValue.from_json(json)
# print the JSON string representation of the object
print(SynchronizedContentReadGrantReturnValue.to_json())

# convert the object into a dict
synchronized_content_read_grant_return_value_dict = synchronized_content_read_grant_return_value_instance.to_dict()
# create an instance of SynchronizedContentReadGrantReturnValue from a dict
synchronized_content_read_grant_return_value_from_dict = SynchronizedContentReadGrantReturnValue.from_dict(synchronized_content_read_grant_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


