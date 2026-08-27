# SynchronizedContentVersionDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**content_version_id** | **UUID** |  | 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**size** | **int** |  | 
**created_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_content_version_dto import SynchronizedContentVersionDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedContentVersionDto from a JSON string
synchronized_content_version_dto_instance = SynchronizedContentVersionDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedContentVersionDto.to_json())

# convert the object into a dict
synchronized_content_version_dto_dict = synchronized_content_version_dto_instance.to_dict()
# create an instance of SynchronizedContentVersionDto from a dict
synchronized_content_version_dto_from_dict = SynchronizedContentVersionDto.from_dict(synchronized_content_version_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


