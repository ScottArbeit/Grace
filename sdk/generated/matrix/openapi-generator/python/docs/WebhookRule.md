# WebhookRule


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**webhook_rule_id** | **UUID** |  | [optional] 
**name** | **str** |  | [optional] 
**event_name** | **str** |  | [optional] 
**event_version** | **int** |  | [optional] 
**scope** | [**WebhookScope**](WebhookScope.md) |  | [optional] 
**url** | [**ScopedOutboundUrl**](ScopedOutboundUrl.md) |  | [optional] 
**signing_secret_version** | **str** |  | [optional] 
**retry_policy** | [**WebhookRetryPolicy**](WebhookRetryPolicy.md) |  | [optional] 
**status** | [**WebhookRuleStatus**](WebhookRuleStatus.md) |  | [optional] 
**created_by** | **str** |  | [optional] 
**created_at** | **datetime** |  | [optional] 
**updated_at** | **datetime** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.webhook_rule import WebhookRule

# TODO update the JSON string below
json = "{}"
# create an instance of WebhookRule from a JSON string
webhook_rule_instance = WebhookRule.from_json(json)
# print the JSON string representation of the object
print(WebhookRule.to_json())

# convert the object into a dict
webhook_rule_dict = webhook_rule_instance.to_dict()
# create an instance of WebhookRule from a dict
webhook_rule_from_dict = WebhookRule.from_dict(webhook_rule_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


