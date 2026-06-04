# ClaimReuseRangesParameters


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
**discovery_operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | [optional] 
**hints** | [**List[ContentBlockReuseRangeHint]**](ContentBlockReuseRangeHint.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.claim_reuse_ranges_parameters import ClaimReuseRangesParameters

# TODO update the JSON string below
json = "{}"
# create an instance of ClaimReuseRangesParameters from a JSON string
claim_reuse_ranges_parameters_instance = ClaimReuseRangesParameters.from_json(json)
# print the JSON string representation of the object
print(ClaimReuseRangesParameters.to_json())

# convert the object into a dict
claim_reuse_ranges_parameters_dict = claim_reuse_ranges_parameters_instance.to_dict()
# create an instance of ClaimReuseRangesParameters from a dict
claim_reuse_ranges_parameters_from_dict = ClaimReuseRangesParameters.from_dict(claim_reuse_ranges_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


