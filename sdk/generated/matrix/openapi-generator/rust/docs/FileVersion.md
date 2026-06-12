# FileVersion

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**relative_path** | Option<**String**> |  | [optional]
**sha256_hash** | Option<**String**> | Lowercase or uppercase 64-character SHA-256 version hash retained for compatibility. | [optional]
**blake3_hash** | Option<**String**> | Lowercase or uppercase 64-character BLAKE3 version hash used for new version graph lookups. | [optional]
**is_binary** | Option<**bool**> |  | [optional]
**size** | Option<**i64**> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**blob_uri** | Option<**String**> |  | [optional]
**content_reference** | Option<[**models::FileContentReference**](FileContentReference.md)> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


