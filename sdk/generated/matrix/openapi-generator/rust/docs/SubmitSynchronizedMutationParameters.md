# SubmitSynchronizedMutationParameters

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | Option<**String**> | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional]
**principal** | Option<**String**> | The entity on whose behalf the action is being performed. | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**owner_name** | Option<**String**> |  | [optional]
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_name** | Option<**String**> |  | [optional]
**repository_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_name** | Option<**String**> |  | [optional]
**operation_id** | **uuid::Uuid** |  | 
**root_configuration_version** | **uuid::Uuid** |  | 
**mutation_kind** | [**models::SynchronizedMutationKind**](SynchronizedMutationKind.md) |  | 
**item_kind** | [**models::SynchronizedItemKind**](SynchronizedItemKind.md) |  | 
**item_id** | Option<**uuid::Uuid**> |  | [optional]
**namespace_precondition** | Option<[**models::SynchronizedNamespacePreconditionDto**](SynchronizedNamespacePreconditionDto.md)> |  | [optional]
**content_precondition** | Option<[**models::SynchronizedContentPreconditionDto**](SynchronizedContentPreconditionDto.md)> |  | [optional]
**creation_slot_expectation** | Option<[**models::SynchronizedCreationSlotExpectationDto**](SynchronizedCreationSlotExpectationDto.md)> |  | [optional]
**destination_parent** | Option<[**models::SynchronizedParentDto**](SynchronizedParentDto.md)> |  | [optional]
**destination_name** | Option<**String**> |  | [optional]
**prepared_content_id** | Option<**uuid::Uuid**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


