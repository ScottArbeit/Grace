# SubmitSynchronizedMutationParameters


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
**operation_id** | **UUID** |  | 
**root_configuration_version** | **UUID** |  | 
**mutation_kind** | [**SynchronizedMutationKind**](SynchronizedMutationKind.md) |  | 
**item_kind** | [**SynchronizedItemKind**](SynchronizedItemKind.md) |  | 
**item_id** | **UUID** |  | [optional] 
**namespace_precondition** | [**SynchronizedNamespacePreconditionDto**](SynchronizedNamespacePreconditionDto.md) |  | [optional] 
**content_precondition** | [**SynchronizedContentPreconditionDto**](SynchronizedContentPreconditionDto.md) |  | [optional] 
**creation_slot_expectation** | [**SynchronizedCreationSlotExpectationDto**](SynchronizedCreationSlotExpectationDto.md) |  | [optional] 
**destination_parent** | [**SynchronizedParentDto**](SynchronizedParentDto.md) |  | [optional] 
**destination_name** | **str** |  | [optional] 
**prepared_content_id** | **UUID** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.submit_synchronized_mutation_parameters import SubmitSynchronizedMutationParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SubmitSynchronizedMutationParameters from a JSON string
submit_synchronized_mutation_parameters_instance = SubmitSynchronizedMutationParameters.from_json(json)
# print the JSON string representation of the object
print(SubmitSynchronizedMutationParameters.to_json())

# convert the object into a dict
submit_synchronized_mutation_parameters_dict = submit_synchronized_mutation_parameters_instance.to_dict()
# create an instance of SubmitSynchronizedMutationParameters from a dict
submit_synchronized_mutation_parameters_from_dict = SubmitSynchronizedMutationParameters.from_dict(submit_synchronized_mutation_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


