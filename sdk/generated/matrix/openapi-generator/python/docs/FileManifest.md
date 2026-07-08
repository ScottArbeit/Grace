# FileManifest

Server-accepted reconstruction contract for one manifest-backed file. StoragePoolId is placement evidence selected by Grace Server, not a publication right for a client to choose physical storage shards.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**manifest_address** | **str** | Lowercase 64-character BLAKE3-derived FileManifest address. | 
**chunking_suite_id** | **str** | Versioned chunking suite identifier. | 
**file_content_hash** | **str** | Lowercase 64-character BLAKE3 hash of the complete unencoded file bytes. | 
**storage_pool_id** | **str** | StoragePool-wide CAS scope identifier. Public clients treat this as server-provided placement evidence and must not use it to select storage accounts, containers, buckets, prefixes, or write authorization directly. | 
**size** | **int** |  | 
**blocks** | [**List[ContentBlock]**](ContentBlock.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.file_manifest import FileManifest

# TODO update the JSON string below
json = "{}"
# create an instance of FileManifest from a JSON string
file_manifest_instance = FileManifest.from_json(json)
# print the JSON string representation of the object
print(FileManifest.to_json())

# convert the object into a dict
file_manifest_dict = file_manifest_instance.to_dict()
# create an instance of FileManifest from a dict
file_manifest_from_dict = FileManifest.from_dict(file_manifest_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


