# OwnerDirectoryVersionObservationScope


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**owner_id** | **UUID** |  | 
**organization_id** | **UUID** |  | 
**repository_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.owner_directory_version_observation_scope import OwnerDirectoryVersionObservationScope

# TODO update the JSON string below
json = "{}"
# create an instance of OwnerDirectoryVersionObservationScope from a JSON string
owner_directory_version_observation_scope_instance = OwnerDirectoryVersionObservationScope.from_json(json)
# print the JSON string representation of the object
print(OwnerDirectoryVersionObservationScope.to_json())

# convert the object into a dict
owner_directory_version_observation_scope_dict = owner_directory_version_observation_scope_instance.to_dict()
# create an instance of OwnerDirectoryVersionObservationScope from a dict
owner_directory_version_observation_scope_from_dict = OwnerDirectoryVersionObservationScope.from_dict(owner_directory_version_observation_scope_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


