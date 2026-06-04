# FinalizeManifestBlockPayload

Optional inline payload used by manifest finalization compatibility paths.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**address** | **str** | Lowercase 64-character BLAKE3-derived ContentBlock address. | [optional] 
**payload** | **bytes** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.finalize_manifest_block_payload import FinalizeManifestBlockPayload

# TODO update the JSON string below
json = "{}"
# create an instance of FinalizeManifestBlockPayload from a JSON string
finalize_manifest_block_payload_instance = FinalizeManifestBlockPayload.from_json(json)
# print the JSON string representation of the object
print(FinalizeManifestBlockPayload.to_json())

# convert the object into a dict
finalize_manifest_block_payload_dict = finalize_manifest_block_payload_instance.to_dict()
# create an instance of FinalizeManifestBlockPayload from a dict
finalize_manifest_block_payload_from_dict = FinalizeManifestBlockPayload.from_dict(finalize_manifest_block_payload_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


