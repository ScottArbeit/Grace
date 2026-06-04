# ConfirmedBlockUpload

Confirmed object-storage placement for an uploaded ContentBlock.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**content_block_address** | **str** | Lowercase 64-character BLAKE3-derived ContentBlock address. | 
**payload_length** | **int** |  | 
**storage_placement** | [**ContentBlockStoragePlacement**](ContentBlockStoragePlacement.md) |  | 
**ranges** | [**List[ContentBlockMetadataRange]**](ContentBlockMetadataRange.md) |  | 
**confirmed_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.confirmed_block_upload import ConfirmedBlockUpload

# TODO update the JSON string below
json = "{}"
# create an instance of ConfirmedBlockUpload from a JSON string
confirmed_block_upload_instance = ConfirmedBlockUpload.from_json(json)
# print the JSON string representation of the object
print(ConfirmedBlockUpload.to_json())

# convert the object into a dict
confirmed_block_upload_dict = confirmed_block_upload_instance.to_dict()
# create an instance of ConfirmedBlockUpload from a dict
confirmed_block_upload_from_dict = ConfirmedBlockUpload.from_dict(confirmed_block_upload_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


