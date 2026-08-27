# SynchronizedRootMutationResultDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **UUID** |  | 
**outcome** | [**SynchronizedOutcomeKind**](SynchronizedOutcomeKind.md) |  | 
**root_configuration** | [**SynchronizedRootConfigurationDto**](SynchronizedRootConfigurationDto.md) |  | 
**reason_code** | **str** |  | 
**recorded_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_root_mutation_result_dto import SynchronizedRootMutationResultDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedRootMutationResultDto from a JSON string
synchronized_root_mutation_result_dto_instance = SynchronizedRootMutationResultDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedRootMutationResultDto.to_json())

# convert the object into a dict
synchronized_root_mutation_result_dto_dict = synchronized_root_mutation_result_dto_instance.to_dict()
# create an instance of SynchronizedRootMutationResultDto from a dict
synchronized_root_mutation_result_dto_from_dict = SynchronizedRootMutationResultDto.from_dict(synchronized_root_mutation_result_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


