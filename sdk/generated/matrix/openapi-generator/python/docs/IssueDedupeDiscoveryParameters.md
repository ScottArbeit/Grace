# IssueDedupeDiscoveryParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **str** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **str** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **str** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**upload_session_id** | **UUID** |  | [optional] 
**authorized_scope** | **str** |  | [optional] 
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | [optional] 
**expires_at** | **datetime** |  | [optional] 
**minimum_reuse_run_length** | **int** |  | [optional] 
**key_chunk_addresses** | **List[str]** |  | [optional] 
**hints** | [**List[ContentBlockReuseRangeHint]**](ContentBlockReuseRangeHint.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.issue_dedupe_discovery_parameters import IssueDedupeDiscoveryParameters

# TODO update the JSON string below
json = "{}"
# create an instance of IssueDedupeDiscoveryParameters from a JSON string
issue_dedupe_discovery_parameters_instance = IssueDedupeDiscoveryParameters.from_json(json)
# print the JSON string representation of the object
print(IssueDedupeDiscoveryParameters.to_json())

# convert the object into a dict
issue_dedupe_discovery_parameters_dict = issue_dedupe_discovery_parameters_instance.to_dict()
# create an instance of IssueDedupeDiscoveryParameters from a dict
issue_dedupe_discovery_parameters_from_dict = IssueDedupeDiscoveryParameters.from_dict(issue_dedupe_discovery_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


