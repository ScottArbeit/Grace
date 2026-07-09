# MaterializationPlanReturnValue

Grace response envelope whose ReturnValue contains a Materialization Plan.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**MaterializationPlan**](MaterializationPlan.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.materialization_plan_return_value import MaterializationPlanReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of MaterializationPlanReturnValue from a JSON string
materialization_plan_return_value_instance = MaterializationPlanReturnValue.from_json(json)
# print the JSON string representation of the object
print(MaterializationPlanReturnValue.to_json())

# convert the object into a dict
materialization_plan_return_value_dict = materialization_plan_return_value_instance.to_dict()
# create an instance of MaterializationPlanReturnValue from a dict
materialization_plan_return_value_from_dict = MaterializationPlanReturnValue.from_dict(materialization_plan_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


