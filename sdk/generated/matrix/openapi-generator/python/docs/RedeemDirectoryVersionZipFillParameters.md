# RedeemDirectoryVersionZipFillParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**permit** | **str** |  | 
**signature** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.redeem_directory_version_zip_fill_parameters import RedeemDirectoryVersionZipFillParameters

# TODO update the JSON string below
json = "{}"
# create an instance of RedeemDirectoryVersionZipFillParameters from a JSON string
redeem_directory_version_zip_fill_parameters_instance = RedeemDirectoryVersionZipFillParameters.from_json(json)
# print the JSON string representation of the object
print(RedeemDirectoryVersionZipFillParameters.to_json())

# convert the object into a dict
redeem_directory_version_zip_fill_parameters_dict = redeem_directory_version_zip_fill_parameters_instance.to_dict()
# create an instance of RedeemDirectoryVersionZipFillParameters from a dict
redeem_directory_version_zip_fill_parameters_from_dict = RedeemDirectoryVersionZipFillParameters.from_dict(redeem_directory_version_zip_fill_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


