# SynchronizedContentAvailable

Content-free best-effort wake. Authorized clients pull durable deltas after receipt.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_name** | **str** |  | 
**repository_id** | **UUID** |  | 
**cursor_epoch** | **str** | Opaque repository epoch. Clients compare only exact equality. | 
**available_after_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**root_configuration_version** | **UUID** |  | 
**occurred_at** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_content_available import SynchronizedContentAvailable

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedContentAvailable from a JSON string
synchronized_content_available_instance = SynchronizedContentAvailable.from_json(json)
# print the JSON string representation of the object
print(SynchronizedContentAvailable.to_json())

# convert the object into a dict
synchronized_content_available_dict = synchronized_content_available_instance.to_dict()
# create an instance of SynchronizedContentAvailable from a dict
synchronized_content_available_from_dict = SynchronizedContentAvailable.from_dict(synchronized_content_available_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


