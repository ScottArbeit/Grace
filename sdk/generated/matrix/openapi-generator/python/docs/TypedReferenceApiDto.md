# TypedReferenceApiDto

A complete real Reference or the canonical absence sentinel for one type-specific latest slot.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**reference_id** | **UUID** |  | 
**owner_id** | **UUID** |  | 
**organization_id** | **UUID** |  | 
**repository_id** | **UUID** |  | 
**branch_id** | **UUID** |  | 
**directory_id** | **UUID** |  | 
**sha256_hash** | **str** |  | 
**blake3_hash** | **str** |  | 
**reference_type** | **str** |  | 
**reference_text** | **str** |  | 
**links** | **List[str]** |  | 
**created_at** | **str** |  | 
**updated_at** | **str** | Null-only audit value from ReferenceDto.Default; non-null strings cannot match the empty-string pattern and minimum length together. | [optional] 
**deleted_at** | **str** | Null-only audit value from ReferenceDto.Default; non-null strings cannot match the empty-string pattern and minimum length together. | [optional] 
**delete_reason** | **str** |  | 
**created_by** | **str** | Null-only audit value from ReferenceDto.Default; non-null strings cannot match the empty-string pattern and minimum length together. | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.typed_reference_api_dto import TypedReferenceApiDto

# TODO update the JSON string below
json = "{}"
# create an instance of TypedReferenceApiDto from a JSON string
typed_reference_api_dto_instance = TypedReferenceApiDto.from_json(json)
# print the JSON string representation of the object
print(TypedReferenceApiDto.to_json())

# convert the object into a dict
typed_reference_api_dto_dict = typed_reference_api_dto_instance.to_dict()
# create an instance of TypedReferenceApiDto from a dict
typed_reference_api_dto_from_dict = TypedReferenceApiDto.from_dict(typed_reference_api_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


