# UploadMetadataArrayReturnValue

Successful upload metadata response for whole-file compatibility uploads.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** |  | 
**properties** | **Dict[str, object]** |  | 
**return_value** | [**List[UploadMetadata]**](UploadMetadata.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_metadata_array_return_value import UploadMetadataArrayReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of UploadMetadataArrayReturnValue from a JSON string
upload_metadata_array_return_value_instance = UploadMetadataArrayReturnValue.from_json(json)
# print the JSON string representation of the object
print(UploadMetadataArrayReturnValue.to_json())

# convert the object into a dict
upload_metadata_array_return_value_dict = upload_metadata_array_return_value_instance.to_dict()
# create an instance of UploadMetadataArrayReturnValue from a dict
upload_metadata_array_return_value_from_dict = UploadMetadataArrayReturnValue.from_dict(upload_metadata_array_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


