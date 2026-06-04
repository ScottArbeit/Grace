# WebhookDelivery


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**webhook_delivery_id** | **UUID** |  | [optional] 
**webhook_rule_id** | **UUID** |  | [optional] 
**event_name** | **str** |  | [optional] 
**event_version** | **int** |  | [optional] 
**dedupe_key** | **str** |  | [optional] 
**status** | [**WebhookDeliveryStatus**](WebhookDeliveryStatus.md) |  | [optional] 
**attempt_count** | **int** |  | [optional] 
**last_attempt_at** | **datetime** |  | [optional] 
**next_attempt_at** | **datetime** |  | [optional] 
**last_status_code** | **int** |  | [optional] 
**last_error** | **str** |  | [optional] 
**created_at** | **datetime** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.webhook_delivery import WebhookDelivery

# TODO update the JSON string below
json = "{}"
# create an instance of WebhookDelivery from a JSON string
webhook_delivery_instance = WebhookDelivery.from_json(json)
# print the JSON string representation of the object
print(WebhookDelivery.to_json())

# convert the object into a dict
webhook_delivery_dict = webhook_delivery_instance.to_dict()
# create an instance of WebhookDelivery from a dict
webhook_delivery_from_dict = WebhookDelivery.from_dict(webhook_delivery_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


