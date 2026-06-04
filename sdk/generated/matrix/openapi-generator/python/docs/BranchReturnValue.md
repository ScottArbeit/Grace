# BranchReturnValue

Grace response envelope whose ReturnValue contains a branch.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**BranchApiDto**](BranchApiDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.branch_return_value import BranchReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of BranchReturnValue from a JSON string
branch_return_value_instance = BranchReturnValue.from_json(json)
# print the JSON string representation of the object
print(BranchReturnValue.to_json())

# convert the object into a dict
branch_return_value_dict = branch_return_value_instance.to_dict()
# create an instance of BranchReturnValue from a dict
branch_return_value_from_dict = BranchReturnValue.from_dict(branch_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


