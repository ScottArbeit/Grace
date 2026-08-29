# LibraryOperationReceiptDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **UUID** |  | 
**request_hash** | **str** |  | 
**outcome** | [**LibraryOutcomeKind**](LibraryOutcomeKind.md) |  | 
**library_catalog_version** | **UUID** |  | 
**recorded_at** | **datetime** |  | 
**principal_id** | **str** |  | 
**change** | [**LibraryChangeDto**](LibraryChangeDto.md) |  | 
**cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**item** | [**LibraryItemDto**](LibraryItemDto.md) |  | 
**conflict** | [**LibraryConflictProvenanceDto**](LibraryConflictProvenanceDto.md) |  | 
**reason_code** | **str** |  | 
**current_library_catalog** | [**LibraryCatalogDto**](LibraryCatalogDto.md) |  | 
**rebaseline** | [**LibraryRebaselineDto**](LibraryRebaselineDto.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_operation_receipt_dto import LibraryOperationReceiptDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryOperationReceiptDto from a JSON string
library_operation_receipt_dto_instance = LibraryOperationReceiptDto.from_json(json)
# print the JSON string representation of the object
print(LibraryOperationReceiptDto.to_json())

# convert the object into a dict
library_operation_receipt_dto_dict = library_operation_receipt_dto_instance.to_dict()
# create an instance of LibraryOperationReceiptDto from a dict
library_operation_receipt_dto_from_dict = LibraryOperationReceiptDto.from_dict(library_operation_receipt_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


