# MaterializationPlan

Resolved immutable target root and artifacts required to materialize it.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**target_root_directory_version_id** | **UUID** |  | 
**execution_mode** | [**MaterializationExecutionMode**](MaterializationExecutionMode.md) |  | 
**cache_selection** | [**MaterializationCacheSelection**](MaterializationCacheSelection.md) |  | 
**required_artifacts** | [**List[MaterializationArtifactDescriptor]**](MaterializationArtifactDescriptor.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.materialization_plan import MaterializationPlan

# TODO update the JSON string below
json = "{}"
# create an instance of MaterializationPlan from a JSON string
materialization_plan_instance = MaterializationPlan.from_json(json)
# print the JSON string representation of the object
print(MaterializationPlan.to_json())

# convert the object into a dict
materialization_plan_dict = materialization_plan_instance.to_dict()
# create an instance of MaterializationPlan from a dict
materialization_plan_from_dict = MaterializationPlan.from_dict(materialization_plan_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


