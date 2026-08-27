# SynchronizedContentReadGrantDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**grant_id** | **str** |  | 
**download_path** | **str** |  | 
**content** | [**SynchronizedContentVersionDto**](SynchronizedContentVersionDto.md) |  | 
**expires_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_content_read_grant_dto import SynchronizedContentReadGrantDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedContentReadGrantDto from a JSON string
synchronized_content_read_grant_dto_instance = SynchronizedContentReadGrantDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedContentReadGrantDto.to_json())

# convert the object into a dict
synchronized_content_read_grant_dto_dict = synchronized_content_read_grant_dto_instance.to_dict()
# create an instance of SynchronizedContentReadGrantDto from a dict
synchronized_content_read_grant_dto_from_dict = SynchronizedContentReadGrantDto.from_dict(synchronized_content_read_grant_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


