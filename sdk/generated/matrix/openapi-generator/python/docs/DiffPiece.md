# DiffPiece


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**position** | **int** |  | [optional] 
**sub_pieces** | **List[object]** |  | [optional] 
**text** | **str** |  | 
**type** | **object** |  | 

## Example

```python
from grace_generated_openapi_probe.models.diff_piece import DiffPiece

# TODO update the JSON string below
json = "{}"
# create an instance of DiffPiece from a JSON string
diff_piece_instance = DiffPiece.from_json(json)
# print the JSON string representation of the object
print(DiffPiece.to_json())

# convert the object into a dict
diff_piece_dict = diff_piece_instance.to_dict()
# create an instance of DiffPiece from a dict
diff_piece_from_dict = DiffPiece.from_dict(diff_piece_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


