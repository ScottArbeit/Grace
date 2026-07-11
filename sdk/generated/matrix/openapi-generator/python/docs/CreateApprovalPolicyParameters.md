# CreateApprovalPolicyParameters


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
**approval_policy_id** | **UUID** |  | [optional] 
**name** | **str** |  | [optional] 
**subject** | **str** |  | [optional] 
**required_responder** | **str** |  | [optional] 
**notification_url** | **str** |  | [optional] 
**notification_url_safety** | [**OutboundUrlSafety**](OutboundUrlSafety.md) |  | [optional] 
**acknowledge_unsafe_local_development** | **bool** |  | [optional] [default to False]
**timeout_seconds** | **int** |  | [optional] 
**on_timeout** | [**ApprovalTimeoutAction**](ApprovalTimeoutAction.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.create_approval_policy_parameters import CreateApprovalPolicyParameters

# TODO update the JSON string below
json = "{}"
# create an instance of CreateApprovalPolicyParameters from a JSON string
create_approval_policy_parameters_instance = CreateApprovalPolicyParameters.from_json(json)
# print the JSON string representation of the object
print(CreateApprovalPolicyParameters.to_json())

# convert the object into a dict
create_approval_policy_parameters_dict = create_approval_policy_parameters_instance.to_dict()
# create an instance of CreateApprovalPolicyParameters from a dict
create_approval_policy_parameters_from_dict = CreateApprovalPolicyParameters.from_dict(create_approval_policy_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


