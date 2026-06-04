# OrganizationReturnValue

Grace response envelope whose ReturnValue contains an organization DTO.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**OrganizationDto**](OrganizationDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.organization_return_value import OrganizationReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of OrganizationReturnValue from a JSON string
organization_return_value_instance = OrganizationReturnValue.from_json(json)
# print the JSON string representation of the object
print(OrganizationReturnValue.to_json())

# convert the object into a dict
organization_return_value_dict = organization_return_value_instance.to_dict()
# create an instance of OrganizationReturnValue from a dict
organization_return_value_from_dict = OrganizationReturnValue.from_dict(organization_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


