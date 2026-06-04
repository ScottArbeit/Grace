# CreateWebhookRuleParameters


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
**name** | **str** |  | [optional] 
**event_name** | **str** |  | [optional] 
**event_version** | **int** |  | [optional] [default to 1]
**url** | **str** |  | [optional] 
**url_safety** | [**OutboundUrlSafety**](OutboundUrlSafety.md) |  | [optional] 
**acknowledge_unsafe_local_development** | **bool** |  | [optional] [default to False]
**signing_secret_version** | **str** |  | [optional] 
**max_attempts** | **int** |  | [optional] [default to 8]
**initial_delay_seconds** | **int** |  | [optional] [default to 30]
**max_delay_seconds** | **int** |  | [optional] [default to 3600]

## Example

```python
from grace_generated_openapi_probe.models.create_webhook_rule_parameters import CreateWebhookRuleParameters

# TODO update the JSON string below
json = "{}"
# create an instance of CreateWebhookRuleParameters from a JSON string
create_webhook_rule_parameters_instance = CreateWebhookRuleParameters.from_json(json)
# print the JSON string representation of the object
print(CreateWebhookRuleParameters.to_json())

# convert the object into a dict
create_webhook_rule_parameters_dict = create_webhook_rule_parameters_instance.to_dict()
# create an instance of CreateWebhookRuleParameters from a dict
create_webhook_rule_parameters_from_dict = CreateWebhookRuleParameters.from_dict(create_webhook_rule_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


