# ReferenceDto

(automatically generated)

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**reference_id** | **UUID** |  | [optional] 
**branch_id** | **UUID** |  | [optional] 
**directory_id** | **UUID** |  | [optional] 
**sha256_hash** | **str** |  | [optional] 
**blake3_hash** | **str** |  | [optional] 
**reference_type** | [**ReferenceType**](ReferenceType.md) |  | [optional] 
**reference_text** | **str** |  | [optional] 
**created_by** | **str** |  | [optional] 
**created_at** | **datetime** |  | [optional] 

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


