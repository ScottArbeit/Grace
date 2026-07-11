# ReferenceDto

(automatically generated)

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**reference_id** | **UUID** |  | 
**branch_id** | **UUID** |  | 
**directory_id** | **UUID** |  | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**reference_type** | [**ReferenceType**](ReferenceType.md) |  | 
**reference_text** | **str** |  | 
**created_by** | **str** |  | [optional] 
**created_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.reference_dto import ReferenceDto

# TODO update the JSON string below
json = "{}"
# create an instance of ReferenceDto from a JSON string
reference_dto_instance = ReferenceDto.from_json(json)
# print the JSON string representation of the object
print(ReferenceDto.to_json())

# convert the object into a dict
reference_dto_dict = reference_dto_instance.to_dict()
# create an instance of ReferenceDto from a dict
reference_dto_from_dict = ReferenceDto.from_dict(reference_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


