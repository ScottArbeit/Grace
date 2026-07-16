# ReferenceApiDto

Public reference DTO returned through branch reference endpoints.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**reference_id** | **UUID** |  | 
**owner_id** | **UUID** |  | 
**organization_id** | **UUID** |  | 
**repository_id** | **UUID** |  | 
**branch_id** | **UUID** |  | 
**directory_id** | **UUID** | DirectoryVersionId represented by the current server DTO field name. | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**reference_type** | [**ReferenceType**](ReferenceType.md) |  | 
**reference_text** | **str** |  | 
**links** | **List[str]** |  | 
**created_at** | **datetime** |  | 
**updated_at** | **datetime** |  | [optional] 
**deleted_at** | **datetime** |  | [optional] 
**delete_reason** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.reference_api_dto import ReferenceApiDto

# TODO update the JSON string below
json = "{}"
# create an instance of ReferenceApiDto from a JSON string
reference_api_dto_instance = ReferenceApiDto.from_json(json)
# print the JSON string representation of the object
print(ReferenceApiDto.to_json())

# convert the object into a dict
reference_api_dto_dict = reference_api_dto_instance.to_dict()
# create an instance of ReferenceApiDto from a dict
reference_api_dto_from_dict = ReferenceApiDto.from_dict(reference_api_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


