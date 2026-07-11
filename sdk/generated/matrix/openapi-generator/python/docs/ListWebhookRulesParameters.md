# ListWebhookRulesParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**properties** | **Dict[str, str]** | Allow-listed event properties. UploadSessionIds is the only client-settable key. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**target_branch_id** | **UUID** |  | [optional] 
**webhook_rule_id** | **UUID** |  | [optional] 
**include_deleted** | **bool** |  | [optional] [default to False]

## Example

```python
from grace_generated_openapi_probe.models.list_webhook_rules_parameters import ListWebhookRulesParameters

# TODO update the JSON string below
json = "{}"
# create an instance of ListWebhookRulesParameters from a JSON string
list_webhook_rules_parameters_instance = ListWebhookRulesParameters.from_json(json)
# print the JSON string representation of the object
print(ListWebhookRulesParameters.to_json())

# convert the object into a dict
list_webhook_rules_parameters_dict = list_webhook_rules_parameters_instance.to_dict()
# create an instance of ListWebhookRulesParameters from a dict
list_webhook_rules_parameters_from_dict = ListWebhookRulesParameters.from_dict(list_webhook_rules_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


