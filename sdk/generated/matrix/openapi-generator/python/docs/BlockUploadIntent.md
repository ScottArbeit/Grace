# BlockUploadIntent

Registered intent to upload a logical ContentBlock range.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**content_block_address** | **str** | Lowercase 64-character BLAKE3-derived ContentBlock address. | 
**logical_offset** | **int** |  | 
**logical_length** | **int** |  | 
**expected_payload_length** | **int** |  | 
**registered_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.block_upload_intent import BlockUploadIntent

# TODO update the JSON string below
json = "{}"
# create an instance of BlockUploadIntent from a JSON string
block_upload_intent_instance = BlockUploadIntent.from_json(json)
# print the JSON string representation of the object
print(BlockUploadIntent.to_json())

# convert the object into a dict
block_upload_intent_dict = block_upload_intent_instance.to_dict()
# create an instance of BlockUploadIntent from a dict
block_upload_intent_from_dict = BlockUploadIntent.from_dict(block_upload_intent_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


