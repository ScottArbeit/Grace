# ReferenceMaterializationBoundaryReturnValue

Grace response envelope containing the root selected for materialization and its ordered event boundary.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**ReferenceMaterializationBoundaryApiDto**](ReferenceMaterializationBoundaryApiDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.reference_materialization_boundary_return_value import ReferenceMaterializationBoundaryReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of ReferenceMaterializationBoundaryReturnValue from a JSON string
reference_materialization_boundary_return_value_instance = ReferenceMaterializationBoundaryReturnValue.from_json(json)
# print the JSON string representation of the object
print(ReferenceMaterializationBoundaryReturnValue.to_json())

# convert the object into a dict
reference_materialization_boundary_return_value_dict = reference_materialization_boundary_return_value_instance.to_dict()
# create an instance of ReferenceMaterializationBoundaryReturnValue from a dict
reference_materialization_boundary_return_value_from_dict = ReferenceMaterializationBoundaryReturnValue.from_dict(reference_materialization_boundary_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


