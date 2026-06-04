# TestWebhookRuleParameters


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
**target_branch_id** | **UUID** |  | [optional] 
**webhook_rule_id** | **UUID** |  | [optional] 
**dedupe_key** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.test_webhook_rule_parameters import TestWebhookRuleParameters

# TODO update the JSON string below
json = "{}"
# create an instance of TestWebhookRuleParameters from a JSON string
test_webhook_rule_parameters_instance = TestWebhookRuleParameters.from_json(json)
# print the JSON string representation of the object
print(TestWebhookRuleParameters.to_json())

# convert the object into a dict
test_webhook_rule_parameters_dict = test_webhook_rule_parameters_instance.to_dict()
# create an instance of TestWebhookRuleParameters from a dict
test_webhook_rule_parameters_from_dict = TestWebhookRuleParameters.from_dict(test_webhook_rule_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


