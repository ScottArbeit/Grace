# GraceError

Grace domain error envelope returned by command handlers. Authentication challenges and framework authorization failures use their HTTP-specific response shapes instead of this domain error envelope.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**error** | **str** |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** |  | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.grace_error import GraceError

# TODO update the JSON string below
json = "{}"
# create an instance of GraceError from a JSON string
grace_error_instance = GraceError.from_json(json)
# print the JSON string representation of the object
print(GraceError.to_json())

# convert the object into a dict
grace_error_dict = grace_error_instance.to_dict()
# create an instance of GraceError from a dict
grace_error_from_dict = GraceError.from_dict(grace_error_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


