# LibraryContentAvailable

Content-free best-effort wake. Authorized clients pull durable changes after receipt.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_name** | **str** |  | 
**repository_id** | **UUID** |  | 
**cursor_epoch** | **str** | Opaque repository epoch. Clients compare only exact equality. | 
**available_after_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**library_catalog_version** | **UUID** |  | 
**occurred_at** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 

## Example

```python
from grace_generated_openapi_probe.models.library_content_available import LibraryContentAvailable

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryContentAvailable from a JSON string
library_content_available_instance = LibraryContentAvailable.from_json(json)
# print the JSON string representation of the object
print(LibraryContentAvailable.to_json())

# convert the object into a dict
library_content_available_dict = library_content_available_instance.to_dict()
# create an instance of LibraryContentAvailable from a dict
library_content_available_from_dict = LibraryContentAvailable.from_dict(library_content_available_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


