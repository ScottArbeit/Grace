# MaterializationArtifactSource

Fetchable or deferred source for one planned artifact.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**source_kind** | [**MaterializationArtifactSourceKind**](MaterializationArtifactSourceKind.md) |  | 
**direct_uri** | **str** |  | [optional] 
**cache_key** | **str** |  | [optional] 
**cache_endpoint** | **str** |  | [optional] 
**cache_id** | **UUID** |  | [optional] 
**direct_fallback_uri** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.materialization_artifact_source import MaterializationArtifactSource

# TODO update the JSON string below
json = "{}"
# create an instance of MaterializationArtifactSource from a JSON string
materialization_artifact_source_instance = MaterializationArtifactSource.from_json(json)
# print the JSON string representation of the object
print(MaterializationArtifactSource.to_json())

# convert the object into a dict
materialization_artifact_source_dict = materialization_artifact_source_instance.to_dict()
# create an instance of MaterializationArtifactSource from a dict
materialization_artifact_source_from_dict = MaterializationArtifactSource.from_dict(materialization_artifact_source_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


