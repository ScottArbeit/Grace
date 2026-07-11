# WebhookDeliveryParameters


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
**webhook_delivery_id** | **UUID** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.webhook_delivery_parameters import WebhookDeliveryParameters

# TODO update the JSON string below
json = "{}"
# create an instance of WebhookDeliveryParameters from a JSON string
webhook_delivery_parameters_instance = WebhookDeliveryParameters.from_json(json)
# print the JSON string representation of the object
print(WebhookDeliveryParameters.to_json())

# convert the object into a dict
webhook_delivery_parameters_dict = webhook_delivery_parameters_instance.to_dict()
# create an instance of WebhookDeliveryParameters from a dict
webhook_delivery_parameters_from_dict = WebhookDeliveryParameters.from_dict(webhook_delivery_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


