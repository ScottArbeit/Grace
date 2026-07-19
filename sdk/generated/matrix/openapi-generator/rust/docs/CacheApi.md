# \CacheApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**assign_cache_repositories**](CacheApi.md#assign_cache_repositories) | **POST** /cache/assign-repositories | Replace a Cache's exact repository assignments as a current administrator.
[**enroll_cache**](CacheApi.md#enroll_cache) | **POST** /cache/enroll | Enroll a Grace Cache with an administrator-authorized repository boundary.
[**get_artifact_grant_validation_keys**](CacheApi.md#get_artifact_grant_validation_keys) | **GET** /cache/validation-keys | Publish artifact grant validation keys.
[**refresh_cache**](CacheApi.md#refresh_cache) | **POST** /cache/refresh | Refresh Cache operational facts with a current identity-key proof.
[**revoke_cache**](CacheApi.md#revoke_cache) | **POST** /cache/revoke | Revoke a Cache registration as a current administrator.
[**submit_cache_key_candidate**](CacheApi.md#submit_cache_key_candidate) | **POST** /cache/candidate | Submit or reuse one Cache identity candidate after active-key proof.



## assign_cache_repositories

> models::CacheRegistrationReturnValue assign_cache_repositories(cache_repository_assignment_request)
Replace a Cache's exact repository assignments as a current administrator.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**cache_repository_assignment_request** | [**CacheRepositoryAssignmentRequest**](CacheRepositoryAssignmentRequest.md) |  | [required] |

### Return type

[**models::CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## enroll_cache

> models::CacheRegistrationReturnValue enroll_cache(cache_enrollment_request)
Enroll a Grace Cache with an administrator-authorized repository boundary.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**cache_enrollment_request** | [**CacheEnrollmentRequest**](CacheEnrollmentRequest.md) |  | [required] |

### Return type

[**models::CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## get_artifact_grant_validation_keys

> models::ArtifactGrantValidationKeySet get_artifact_grant_validation_keys()
Publish artifact grant validation keys.

Public verification material for Grace Cache artifact-grant validation. This operation does not require a Grace user session.

### Parameters

This endpoint does not need any parameter.

### Return type

[**models::ArtifactGrantValidationKeySet**](ArtifactGrantValidationKeySet.md)

### Authorization

No authorization required

### HTTP request headers

- **Content-Type**: Not defined
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## refresh_cache

> models::CacheRegistrationReturnValue refresh_cache(cache_registration_refresh_request)
Refresh Cache operational facts with a current identity-key proof.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**cache_registration_refresh_request** | [**CacheRegistrationRefreshRequest**](CacheRegistrationRefreshRequest.md) |  | [required] |

### Return type

[**models::CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

No authorization required

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## revoke_cache

> models::CacheRegistrationReturnValue revoke_cache(cache_revocation_request)
Revoke a Cache registration as a current administrator.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**cache_revocation_request** | [**CacheRevocationRequest**](CacheRevocationRequest.md) |  | [required] |

### Return type

[**models::CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json, text/plain

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## submit_cache_key_candidate

> models::CacheRegistrationReturnValue submit_cache_key_candidate(cache_key_candidate_request)
Submit or reuse one Cache identity candidate after active-key proof.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**cache_key_candidate_request** | [**CacheKeyCandidateRequest**](CacheKeyCandidateRequest.md) |  | [required] |

### Return type

[**models::CacheRegistrationReturnValue**](CacheRegistrationReturnValue.md)

### Authorization

No authorization required

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

