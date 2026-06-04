# EventMetadata


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**timestamp** | **datetime** |  | [optional] 
**correlation_id** | **str** |  | [optional] 
**principal** | **str** |  | [optional] 
**client_type** | **str** | The Grace client identity type/version recorded for diagnostics. Client identity headers are not authentication proof and must not be used as authorization evidence. | [optional] 
**properties** | **Dict[str, str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.event_metadata import EventMetadata

# TODO update the JSON string below
json = "{}"
# create an instance of EventMetadata from a JSON string
event_metadata_instance = EventMetadata.from_json(json)
# print the JSON string representation of the object
print(EventMetadata.to_json())

# convert the object into a dict
event_metadata_dict = event_metadata_instance.to_dict()
# create an instance of EventMetadata from a dict
event_metadata_from_dict = EventMetadata.from_dict(event_metadata_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


