# SynchronizedMutationDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**operation_id** | **UUID** |  | 
**mutation_kind** | [**SynchronizedMutationKind**](SynchronizedMutationKind.md) |  | 
**item_id** | **UUID** |  | 
**item_kind** | [**SynchronizedItemKind**](SynchronizedItemKind.md) |  | 
**accepted_at** | **datetime** |  | 
**accepted_by** | **str** |  | 
**root_configuration_version** | **UUID** |  | 
**namespace** | [**SynchronizedNamespaceDto**](SynchronizedNamespaceDto.md) |  | 
**content** | [**SynchronizedContentVersionDto**](SynchronizedContentVersionDto.md) |  | 
**tombstone** | [**SynchronizedTombstoneDto**](SynchronizedTombstoneDto.md) |  | 
**conflict** | [**SynchronizedConflictProvenanceDto**](SynchronizedConflictProvenanceDto.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_mutation_dto import SynchronizedMutationDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedMutationDto from a JSON string
synchronized_mutation_dto_instance = SynchronizedMutationDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedMutationDto.to_json())

# convert the object into a dict
synchronized_mutation_dto_dict = synchronized_mutation_dto_instance.to_dict()
# create an instance of SynchronizedMutationDto from a dict
synchronized_mutation_dto_from_dict = SynchronizedMutationDto.from_dict(synchronized_mutation_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


