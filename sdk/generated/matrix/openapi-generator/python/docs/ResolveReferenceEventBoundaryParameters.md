# ResolveReferenceEventBoundaryParameters

Supplies the full local root tuple for missing-cursor recovery. Only a matching Save, Commit, or Checkpoint for the same repository and branch returns its exact cursor; Created and Rebased branch bases, every other Reference kind, and unmatched roots use the same immutable-snapshot tail baseline, even when the tuple matches.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**branch_id** | **UUID** |  | [optional] 
**branch_name** | **str** |  | [optional] 
**directory_version_id** | **UUID** |  | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 

## Example

```python
from grace_generated_openapi_probe.models.resolve_reference_event_boundary_parameters import ResolveReferenceEventBoundaryParameters

# TODO update the JSON string below
json = "{}"
# create an instance of ResolveReferenceEventBoundaryParameters from a JSON string
resolve_reference_event_boundary_parameters_instance = ResolveReferenceEventBoundaryParameters.from_json(json)
# print the JSON string representation of the object
print(ResolveReferenceEventBoundaryParameters.to_json())

# convert the object into a dict
resolve_reference_event_boundary_parameters_dict = resolve_reference_event_boundary_parameters_instance.to_dict()
# create an instance of ResolveReferenceEventBoundaryParameters from a dict
resolve_reference_event_boundary_parameters_from_dict = ResolveReferenceEventBoundaryParameters.from_dict(resolve_reference_event_boundary_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


