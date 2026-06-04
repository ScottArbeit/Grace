# WebhookScope


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**owner_id** | **UUID** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**target_branch_id** | **UUID** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.webhook_scope import WebhookScope

# TODO update the JSON string below
json = "{}"
# create an instance of WebhookScope from a JSON string
webhook_scope_instance = WebhookScope.from_json(json)
# print the JSON string representation of the object
print(WebhookScope.to_json())

# convert the object into a dict
webhook_scope_dict = webhook_scope_instance.to_dict()
# create an instance of WebhookScope from a dict
webhook_scope_from_dict = WebhookScope.from_dict(webhook_scope_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


