# ContentBlock

Logical byte range inside a manifest-backed file.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**address** | **str** | Lowercase 64-character BLAKE3-derived ContentBlock address. | 
**offset** | **int** |  | 
**size** | **int** |  | 

## Example

```python
from grace_generated_openapi_probe.models.content_block import ContentBlock

# TODO update the JSON string below
json = "{}"
# create an instance of ContentBlock from a JSON string
content_block_instance = ContentBlock.from_json(json)
# print the JSON string representation of the object
print(ContentBlock.to_json())

# convert the object into a dict
content_block_dict = content_block_instance.to_dict()
# create an instance of ContentBlock from a dict
content_block_from_dict = ContentBlock.from_dict(content_block_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


