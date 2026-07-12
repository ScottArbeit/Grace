# ReferenceDefaultSentinel

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **Class** |  (enum: ReferenceDto) | 
**reference_id** | **ReferenceId** |  (enum: 00000000-0000-0000-0000-000000000000) | 
**owner_id** | **OwnerId** |  (enum: 00000000-0000-0000-0000-000000000000) | 
**organization_id** | **OrganizationId** |  (enum: 00000000-0000-0000-0000-000000000000) | 
**repository_id** | **RepositoryId** |  (enum: 00000000-0000-0000-0000-000000000000) | 
**branch_id** | **BranchId** |  (enum: 00000000-0000-0000-0000-000000000000) | 
**directory_id** | **DirectoryId** |  (enum: 00000000-0000-0000-0000-000000000000) | 
**sha256_hash** | **Sha256Hash** |  (enum: ) | 
**blake3_hash** | **Blake3Hash** |  (enum: ) | 
**reference_type** | **ReferenceType** |  (enum: Save) | 
**reference_text** | **ReferenceText** |  (enum: ) | 
**links** | **Vec<String>** |  | 
**created_by** | Option<**String**> | Null-only audit value from ReferenceDto.Default; non-null strings cannot match the empty-string pattern and minimum length together. | [optional]
**created_at** | **CreatedAt** |  (enum: 2000-01-01T00:00:00Z) | 
**updated_at** | Option<**String**> | Null-only audit value from ReferenceDto.Default; non-null strings cannot match the empty-string pattern and minimum length together. | [optional]
**deleted_at** | Option<**String**> | Null-only audit value from ReferenceDto.Default; non-null strings cannot match the empty-string pattern and minimum length together. | [optional]
**delete_reason** | **DeleteReason** |  (enum: ) | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


