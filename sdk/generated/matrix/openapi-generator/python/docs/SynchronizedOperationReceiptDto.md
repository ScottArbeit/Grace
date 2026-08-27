# SynchronizedOperationReceiptDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **UUID** |  | 
**request_hash** | **str** |  | 
**outcome** | [**SynchronizedOutcomeKind**](SynchronizedOutcomeKind.md) |  | 
**root_configuration_version** | **UUID** |  | 
**recorded_at** | **datetime** |  | 
**principal_id** | **str** |  | 
**mutation** | [**SynchronizedMutationDto**](SynchronizedMutationDto.md) |  | 
**cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**item** | [**SynchronizedItemDto**](SynchronizedItemDto.md) |  | 
**conflict** | [**SynchronizedConflictProvenanceDto**](SynchronizedConflictProvenanceDto.md) |  | 
**reason_code** | **str** |  | 
**current_root_configuration** | [**SynchronizedRootConfigurationDto**](SynchronizedRootConfigurationDto.md) |  | 
**rebaseline** | [**SynchronizedRebaselineDto**](SynchronizedRebaselineDto.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_operation_receipt_dto import SynchronizedOperationReceiptDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedOperationReceiptDto from a JSON string
synchronized_operation_receipt_dto_instance = SynchronizedOperationReceiptDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedOperationReceiptDto.to_json())

# convert the object into a dict
synchronized_operation_receipt_dto_dict = synchronized_operation_receipt_dto_instance.to_dict()
# create an instance of SynchronizedOperationReceiptDto from a dict
synchronized_operation_receipt_dto_from_dict = SynchronizedOperationReceiptDto.from_dict(synchronized_operation_receipt_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


