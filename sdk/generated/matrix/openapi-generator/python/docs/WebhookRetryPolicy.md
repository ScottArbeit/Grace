# WebhookRetryPolicy


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**max_attempts** | **int** |  | [optional] 
**initial_delay_seconds** | **int** |  | [optional] 
**max_delay_seconds** | **int** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.webhook_retry_policy import WebhookRetryPolicy

# TODO update the JSON string below
json = "{}"
# create an instance of WebhookRetryPolicy from a JSON string
webhook_retry_policy_instance = WebhookRetryPolicy.from_json(json)
# print the JSON string representation of the object
print(WebhookRetryPolicy.to_json())

# convert the object into a dict
webhook_retry_policy_dict = webhook_retry_policy_instance.to_dict()
# create an instance of WebhookRetryPolicy from a dict
webhook_retry_policy_from_dict = WebhookRetryPolicy.from_dict(webhook_retry_policy_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


