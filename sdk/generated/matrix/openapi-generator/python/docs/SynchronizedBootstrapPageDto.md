# SynchronizedBootstrapPageDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**bootstrap_id** | **UUID** |  | 
**boundary_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**cursor_epoch** | **str** | Opaque repository epoch. Clients compare only exact equality. | 
**root_configuration** | [**SynchronizedRootConfigurationDto**](SynchronizedRootConfigurationDto.md) |  | 
**items** | [**List[SynchronizedItemDto]**](SynchronizedItemDto.md) |  | 
**next_page_token** | **str** | Opaque token for continuing one immutable page sequence. | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_bootstrap_page_dto import SynchronizedBootstrapPageDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedBootstrapPageDto from a JSON string
synchronized_bootstrap_page_dto_instance = SynchronizedBootstrapPageDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedBootstrapPageDto.to_json())

# convert the object into a dict
synchronized_bootstrap_page_dto_dict = synchronized_bootstrap_page_dto_instance.to_dict()
# create an instance of SynchronizedBootstrapPageDto from a dict
synchronized_bootstrap_page_dto_from_dict = SynchronizedBootstrapPageDto.from_dict(synchronized_bootstrap_page_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


