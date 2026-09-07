# OwnerDirectoryVersionObservation

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**observation_id** | **uuid::Uuid** |  | 
**scope** | [**models::OwnerDirectoryVersionObservationScope**](OwnerDirectoryVersionObservationScope.md) |  | 
**declared_logical_bytes** | **String** | Declared logical bytes as an exact decimal integer in [0, 9223372036854775807]. | 
**distinct_content_count** | **String** | Distinct content declarations as an exact decimal integer in [0, 9223372036854775807]. | 
**enumeration_started_at** | **String** | Original enumeration start in UTC, using NodaTime Instant ISO serialization, years -9998 through 9999 and up to nine fractional-second digits. Preserve the string; JavaScript Date loses precision. | 
**enumeration_finished_at** | **String** | Original enumeration finish in UTC, using NodaTime Instant ISO serialization, years -9998 through 9999 and up to nine fractional-second digits. This is not the read time or a freshness guarantee. | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


