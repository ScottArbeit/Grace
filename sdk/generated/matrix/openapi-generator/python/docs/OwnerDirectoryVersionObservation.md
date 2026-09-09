# OwnerDirectoryVersionObservation

Retained completed DirectoryVersion metadata declarations. Zero is a completed reading, not missing data. This is not complete storage, elapsed usage or a charge.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**observation_id** | **UUID** |  | 
**scope** | [**OwnerDirectoryVersionObservationScope**](OwnerDirectoryVersionObservationScope.md) |  | 
**declared_logical_bytes** | **str** | Declared logical bytes as an exact decimal integer in [0, 9223372036854775807]. | 
**distinct_content_count** | **str** | Distinct content declarations as an exact decimal integer in [0, 9223372036854775807]. | 
**enumeration_started_at** | **str** | Original enumeration start in UTC, using NodaTime Instant ISO serialization, years -9998 through 9999 and up to nine fractional-second digits. Preserve the string; JavaScript Date loses precision. | 
**enumeration_finished_at** | **str** | Original enumeration finish in UTC, using NodaTime Instant ISO serialization, years -9998 through 9999 and up to nine fractional-second digits. This is not the read time or a freshness guarantee. | 

## Example

```python
from grace_generated_openapi_probe.models.owner_directory_version_observation import OwnerDirectoryVersionObservation

# TODO update the JSON string below
json = "{}"
# create an instance of OwnerDirectoryVersionObservation from a JSON string
owner_directory_version_observation_instance = OwnerDirectoryVersionObservation.from_json(json)
# print the JSON string representation of the object
print(OwnerDirectoryVersionObservation.to_json())

# convert the object into a dict
owner_directory_version_observation_dict = owner_directory_version_observation_instance.to_dict()
# create an instance of OwnerDirectoryVersionObservation from a dict
owner_directory_version_observation_from_dict = OwnerDirectoryVersionObservation.from_dict(owner_directory_version_observation_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


