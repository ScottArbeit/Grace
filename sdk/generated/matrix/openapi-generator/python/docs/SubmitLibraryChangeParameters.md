# SubmitLibraryChangeParameters


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
**library_catalog_version** | **UUID** |  | 
**change_kind** | [**LibraryChangeKind**](LibraryChangeKind.md) |  | 
**item_kind** | [**LibraryItemKind**](LibraryItemKind.md) |  | 
**item_id** | **UUID** |  | [optional] 
**namespace_precondition** | [**LibraryNamespacePreconditionDto**](LibraryNamespacePreconditionDto.md) |  | [optional] 
**content_precondition** | [**LibraryContentPreconditionDto**](LibraryContentPreconditionDto.md) |  | [optional] 
**creation_slot_expectation** | [**LibraryCreationSlotExpectationDto**](LibraryCreationSlotExpectationDto.md) |  | [optional] 
**destination_parent** | [**LibraryParentDto**](LibraryParentDto.md) |  | [optional] 
**destination_name** | **str** |  | [optional] 
**prepared_content_id** | **UUID** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.submit_library_change_parameters import SubmitLibraryChangeParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SubmitLibraryChangeParameters from a JSON string
submit_library_change_parameters_instance = SubmitLibraryChangeParameters.from_json(json)
# print the JSON string representation of the object
print(SubmitLibraryChangeParameters.to_json())

# convert the object into a dict
submit_library_change_parameters_dict = submit_library_change_parameters_instance.to_dict()
# create an instance of SubmitLibraryChangeParameters from a dict
submit_library_change_parameters_from_dict = SubmitLibraryChangeParameters.from_dict(submit_library_change_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


