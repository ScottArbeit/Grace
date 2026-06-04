# UploadMetadata

Upload metadata returned by whole-file compatibility upload planning.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**relative_path** | **str** |  | 
**blob_uri_with_sas_token** | **str** |  | 
**sha256_hash** | **str** |  | 
**content_reference** | [**FileContentReference**](FileContentReference.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_metadata import UploadMetadata

# TODO update the JSON string below
json = "{}"
# create an instance of UploadMetadata from a JSON string
upload_metadata_instance = UploadMetadata.from_json(json)
# print the JSON string representation of the object
print(UploadMetadata.to_json())

# convert the object into a dict
upload_metadata_dict = upload_metadata_instance.to_dict()
# create an instance of UploadMetadata from a dict
upload_metadata_from_dict = UploadMetadata.from_dict(upload_metadata_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


