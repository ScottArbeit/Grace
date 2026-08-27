# PrepareDirectoryVersionZipParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**repository_id** | **UUID** |  | 
**directory_version_id** | **UUID** |  | 
**cache_public_key** | [**P256PublicJwk**](P256PublicJwk.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.prepare_directory_version_zip_parameters import PrepareDirectoryVersionZipParameters

# TODO update the JSON string below
json = "{}"
# create an instance of PrepareDirectoryVersionZipParameters from a JSON string
prepare_directory_version_zip_parameters_instance = PrepareDirectoryVersionZipParameters.from_json(json)
# print the JSON string representation of the object
print(PrepareDirectoryVersionZipParameters.to_json())

# convert the object into a dict
prepare_directory_version_zip_parameters_dict = prepare_directory_version_zip_parameters_instance.to_dict()
# create an instance of PrepareDirectoryVersionZipParameters from a dict
prepare_directory_version_zip_parameters_from_dict = PrepareDirectoryVersionZipParameters.from_dict(prepare_directory_version_zip_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


