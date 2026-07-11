# MaterializationPlanRequest

Request contract for server-side Materialization Plan creation.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**target_selector** | [**MaterializationTargetSelector**](MaterializationTargetSelector.md) |  | 
**execution_mode** | [**MaterializationExecutionMode**](MaterializationExecutionMode.md) |  | 
**cache_selection** | [**MaterializationCacheSelection**](MaterializationCacheSelection.md) |  | 
**requested_artifact_kinds** | [**List[MaterializationArtifactKind]**](MaterializationArtifactKind.md) |  | 
**holder_public_key** | [**ArtifactGrantHolderPublicKey**](ArtifactGrantHolderPublicKey.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.materialization_plan_request import MaterializationPlanRequest

# TODO update the JSON string below
json = "{}"
# create an instance of MaterializationPlanRequest from a JSON string
materialization_plan_request_instance = MaterializationPlanRequest.from_json(json)
# print the JSON string representation of the object
print(MaterializationPlanRequest.to_json())

# convert the object into a dict
materialization_plan_request_dict = materialization_plan_request_instance.to_dict()
# create an instance of MaterializationPlanRequest from a dict
materialization_plan_request_from_dict = MaterializationPlanRequest.from_dict(materialization_plan_request_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


