# MaterializationArtifactDescriptor

Stable artifact identity required to materialize a resolved target root.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**artifact_kind** | [**MaterializationArtifactKind**](MaterializationArtifactKind.md) |  | 
**canonical_artifact_identity** | **str** |  | [optional] 
**represented_root_directory_version_id** | **UUID** |  | [optional] 
**target_root_directory_version_id** | **UUID** |  | 
**size_in_bytes** | **int** |  | [optional] 
**relative_path** | **str** |  | [optional] 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | [optional] 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | [optional] 
**manifest_address** | **str** | Lowercase 64-character BLAKE3-derived FileManifest address. | [optional] 
**content_block_address** | **str** | Lowercase 64-character BLAKE3-derived ContentBlock address. | [optional] 
**storage_pool_id** | **str** | StoragePool-wide CAS scope identifier. Public clients treat this as server-provided placement evidence and must not use it to select storage accounts, containers, buckets, prefixes, or write authority directly. | [optional] 
**source** | [**MaterializationArtifactSource**](MaterializationArtifactSource.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.materialization_artifact_descriptor import MaterializationArtifactDescriptor

# TODO update the JSON string below
json = "{}"
# create an instance of MaterializationArtifactDescriptor from a JSON string
materialization_artifact_descriptor_instance = MaterializationArtifactDescriptor.from_json(json)
# print the JSON string representation of the object
print(MaterializationArtifactDescriptor.to_json())

# convert the object into a dict
materialization_artifact_descriptor_dict = materialization_artifact_descriptor_instance.to_dict()
# create an instance of MaterializationArtifactDescriptor from a dict
materialization_artifact_descriptor_from_dict = MaterializationArtifactDescriptor.from_dict(materialization_artifact_descriptor_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


