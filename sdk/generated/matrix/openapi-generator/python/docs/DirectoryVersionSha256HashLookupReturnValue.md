# DirectoryVersionSha256HashLookupReturnValue

Grace response envelope whose ReturnValue contains a SHA-256 lookup result or no-match sentinel.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**DirectoryVersionHashLookupResult**](DirectoryVersionHashLookupResult.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.directory_version_sha256_hash_lookup_return_value import DirectoryVersionSha256HashLookupReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of DirectoryVersionSha256HashLookupReturnValue from a JSON string
directory_version_sha256_hash_lookup_return_value_instance = DirectoryVersionSha256HashLookupReturnValue.from_json(json)
# print the JSON string representation of the object
print(DirectoryVersionSha256HashLookupReturnValue.to_json())

# convert the object into a dict
directory_version_sha256_hash_lookup_return_value_dict = directory_version_sha256_hash_lookup_return_value_instance.to_dict()
# create an instance of DirectoryVersionSha256HashLookupReturnValue from a dict
directory_version_sha256_hash_lookup_return_value_from_dict = DirectoryVersionSha256HashLookupReturnValue.from_dict(directory_version_sha256_hash_lookup_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


