# CacheRegistration

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**cache_id** | **uuid::Uuid** |  | 
**display_name** | **String** |  | 
**boundary_kind** | [**models::CacheBoundaryKind**](CacheBoundaryKind.md) |  | 
**owner_id** | **uuid::Uuid** |  | 
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_scopes** | [**Vec<models::CacheRepositoryScope>**](CacheRepositoryScope.md) |  | 
**public_key** | [**models::CacheIdentityPublicKey**](CacheIdentityPublicKey.md) |  | 
**endpoint** | **String** |  | 
**allow_http_endpoint** | **bool** | Persisted administrator approval for this exact Endpoint to use HTTP instead of HTTPS. | 
**health** | [**models::CacheHealthStatus**](CacheHealthStatus.md) |  | 
**software_version** | **String** |  | 
**protocol_version** | **String** |  | 
**prefetch_supported** | **bool** |  | 
**enrolled_by** | **String** |  | 
**enrolled_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**last_refreshed_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**refresh_after** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**expires_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**rotation_due_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**revoked_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


