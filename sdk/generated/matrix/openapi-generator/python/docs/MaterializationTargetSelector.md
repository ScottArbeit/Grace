# MaterializationTargetSelector

Public selector that the server resolves to one immutable root DirectoryVersionId.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**selector_kind** | [**MaterializationTargetSelectorKind**](MaterializationTargetSelectorKind.md) |  | 
**directory_version_id** | **UUID** |  | [optional] 
**reference_id** | **UUID** |  | [optional] 
**branch_name** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.materialization_target_selector import MaterializationTargetSelector

# TODO update the JSON string below
json = "{}"
# create an instance of MaterializationTargetSelector from a JSON string
materialization_target_selector_instance = MaterializationTargetSelector.from_json(json)
# print the JSON string representation of the object
print(MaterializationTargetSelector.to_json())

# convert the object into a dict
materialization_target_selector_dict = materialization_target_selector_instance.to_dict()
# create an instance of MaterializationTargetSelector from a dict
materialization_target_selector_from_dict = MaterializationTargetSelector.from_dict(materialization_target_selector_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


