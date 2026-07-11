# ApproveApprovalRequestParameters


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
**approval_request_id** | **UUID** |  | [optional] 
**reason** | **str** |  | [optional] 
**client_decision_id** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.approve_approval_request_parameters import ApproveApprovalRequestParameters

# TODO update the JSON string below
json = "{}"
# create an instance of ApproveApprovalRequestParameters from a JSON string
approve_approval_request_parameters_instance = ApproveApprovalRequestParameters.from_json(json)
# print the JSON string representation of the object
print(ApproveApprovalRequestParameters.to_json())

# convert the object into a dict
approve_approval_request_parameters_dict = approve_approval_request_parameters_instance.to_dict()
# create an instance of ApproveApprovalRequestParameters from a dict
approve_approval_request_parameters_from_dict = ApproveApprovalRequestParameters.from_dict(approve_approval_request_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


