# ReferenceDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**reference_id** | Option<**uuid::Uuid**> |  | [optional]
**branch_id** | Option<**uuid::Uuid**> |  | [optional]
**directory_id** | Option<**uuid::Uuid**> |  | [optional]
**sha256_hash** | Option<**String**> | Lowercase or uppercase 64-character SHA-256 version hash retained for compatibility. | [optional]
**blake3_hash** | Option<**String**> | Lowercase or uppercase 64-character BLAKE3 version hash used for new version graph lookups. | [optional]
**reference_type** | Option<[**models::ReferenceType**](ReferenceType.md)> |  | [optional]
**reference_text** | Option<**String**> |  | [optional]
**created_by** | Option<**String**> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


