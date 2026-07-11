# ArtifactGrantHeader

Canonical header for one signed artifact grant.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**algorithm** | **str** |  | 
**key_id** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.artifact_grant_header import ArtifactGrantHeader

# TODO update the JSON string below
json = "{}"
# create an instance of ArtifactGrantHeader from a JSON string
artifact_grant_header_instance = ArtifactGrantHeader.from_json(json)
# print the JSON string representation of the object
print(ArtifactGrantHeader.to_json())

# convert the object into a dict
artifact_grant_header_dict = artifact_grant_header_instance.to_dict()
# create an instance of ArtifactGrantHeader from a dict
artifact_grant_header_from_dict = ArtifactGrantHeader.from_dict(artifact_grant_header_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


