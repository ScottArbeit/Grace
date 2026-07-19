# CacheEnrollmentRequest

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**display_name** | **String** |  | 
**boundary_kind** | [**models::CacheBoundaryKind**](CacheBoundaryKind.md) |  | 
**owner_id** | **uuid::Uuid** |  | 
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_scopes** | [**Vec<models::CacheRepositoryScope>**](CacheRepositoryScope.md) |  | 
**public_key** | [**models::CacheIdentityPublicKey**](CacheIdentityPublicKey.md) |  | 
**endpoint** | **String** | Absolute HTTP(S) Cache origin with path '/', no user info, query, or fragment. | 
**allow_http_endpoint** | **bool** | Explicit administrator approval for this exact Endpoint to use HTTP instead of the HTTPS default. | 
**software_version** | **String** |  | 
**protocol_version** | **String** |  | 
**prefetch_supported** | **bool** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


