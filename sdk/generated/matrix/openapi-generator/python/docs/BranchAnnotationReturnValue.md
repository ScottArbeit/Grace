# BranchAnnotationReturnValue

Grace response envelope whose ReturnValue contains a branch annotation.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**BranchAnnotationApiDto**](BranchAnnotationApiDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.branch_annotation_return_value import BranchAnnotationReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of BranchAnnotationReturnValue from a JSON string
branch_annotation_return_value_instance = BranchAnnotationReturnValue.from_json(json)
# print the JSON string representation of the object
print(BranchAnnotationReturnValue.to_json())

# convert the object into a dict
branch_annotation_return_value_dict = branch_annotation_return_value_instance.to_dict()
# create an instance of BranchAnnotationReturnValue from a dict
branch_annotation_return_value_from_dict = BranchAnnotationReturnValue.from_dict(branch_annotation_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


