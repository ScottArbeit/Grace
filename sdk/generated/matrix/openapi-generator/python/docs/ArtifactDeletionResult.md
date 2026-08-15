# ArtifactDeletionResult


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**artifact_id** | **UUID** |  | 
**work_item_id** | **UUID** |  | 
**deletion_generation** | **UUID** |  | 
**deleted_at** | **datetime** |  | 
**physical_deletion_at** | **datetime** |  | 
**delete_reason** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.artifact_deletion_result import ArtifactDeletionResult

# TODO update the JSON string below
json = "{}"
# create an instance of ArtifactDeletionResult from a JSON string
artifact_deletion_result_instance = ArtifactDeletionResult.from_json(json)
# print the JSON string representation of the object
print(ArtifactDeletionResult.to_json())

# convert the object into a dict
artifact_deletion_result_dict = artifact_deletion_result_instance.to_dict()
# create an instance of ArtifactDeletionResult from a dict
artifact_deletion_result_from_dict = ArtifactDeletionResult.from_dict(artifact_deletion_result_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


