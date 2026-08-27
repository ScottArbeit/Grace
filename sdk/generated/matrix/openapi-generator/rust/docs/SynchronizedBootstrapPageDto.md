# SynchronizedBootstrapPageDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**bootstrap_id** | **uuid::Uuid** |  | 
**boundary_cursor** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**cursor_epoch** | **String** | Opaque repository epoch. Clients compare only exact equality. | 
**root_configuration** | [**models::SynchronizedRootConfigurationDto**](SynchronizedRootConfigurationDto.md) |  | 
**items** | [**Vec<models::SynchronizedItemDto>**](SynchronizedItemDto.md) |  | 
**next_page_token** | **String** | Opaque token for continuing one immutable page sequence. | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


