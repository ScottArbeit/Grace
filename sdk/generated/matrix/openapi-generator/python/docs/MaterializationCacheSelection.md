# MaterializationCacheSelection

Cache-selection intent separate from artifact source details.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**selection_kind** | [**MaterializationCacheSelectionKind**](MaterializationCacheSelectionKind.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.materialization_cache_selection import MaterializationCacheSelection

# TODO update the JSON string below
json = "{}"
# create an instance of MaterializationCacheSelection from a JSON string
materialization_cache_selection_instance = MaterializationCacheSelection.from_json(json)
# print the JSON string representation of the object
print(MaterializationCacheSelection.to_json())

# convert the object into a dict
materialization_cache_selection_dict = materialization_cache_selection_instance.to_dict()
# create an instance of MaterializationCacheSelection from a dict
materialization_cache_selection_from_dict = MaterializationCacheSelection.from_dict(materialization_cache_selection_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


