# CacheRegistrationRequest

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**endpoint** | **String** |  | 
**requested_scopes** | **Vec<String>** | Materialization-plan selection requires stable `repository:<OwnerId>/<OrganizationId>/<RepositoryId>` scopes; repository names and `storage-pool:*` scopes do not match, and multi-repository Cache registrations list each repository scope explicitly. | 
**requested_capabilities** | **Vec<String>** |  | 
**requested_execution_modes** | [**Vec<models::MaterializationExecutionMode>**](MaterializationExecutionMode.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


