# ReferenceDefaultSentinel

Canonical ReferenceDto.Default value used only when a type-specific latest Reference does not yet exist.

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
**created_by** | **str** | Null-only audit value from ReferenceDto.Default; non-null strings cannot match the empty-string pattern and minimum length together. | [optional] 
**created_at** | **str** |  | 
**updated_at** | **str** | Null-only audit value from ReferenceDto.Default; non-null strings cannot match the empty-string pattern and minimum length together. | [optional] 
**deleted_at** | **str** | Null-only audit value from ReferenceDto.Default; non-null strings cannot match the empty-string pattern and minimum length together. | [optional] 
**delete_reason** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.reference_default_sentinel import ReferenceDefaultSentinel

# TODO update the JSON string below
json = "{}"
# create an instance of ReferenceDefaultSentinel from a JSON string
reference_default_sentinel_instance = ReferenceDefaultSentinel.from_json(json)
# print the JSON string representation of the object
print(ReferenceDefaultSentinel.to_json())

# convert the object into a dict
reference_default_sentinel_dict = reference_default_sentinel_instance.to_dict()
# create an instance of ReferenceDefaultSentinel from a dict
reference_default_sentinel_from_dict = ReferenceDefaultSentinel.from_dict(reference_default_sentinel_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


