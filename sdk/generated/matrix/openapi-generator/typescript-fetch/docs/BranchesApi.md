# BranchesApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**annotateBranch**](BranchesApi.md#annotatebranch) | **POST** /branch/annotate | Annotate a branch reference. |
| [**checkpointBranch**](BranchesApi.md#checkpointbranch) | **POST** /branch/checkpoint | Checkpoint the current branch content. |
| [**commitBranch**](BranchesApi.md#commitbranch) | **POST** /branch/commit | Commit the current branch content. |
| [**createBranch**](BranchesApi.md#createbranch) | **POST** /branch/create | Create a branch. |
| [**deleteBranch**](BranchesApi.md#deletebranch) | **POST** /branch/delete | Delete a branch. |
| [**enableBranchCheckpoint**](BranchesApi.md#enablebranchcheckpoint) | **POST** /branch/enableCheckpoint | Enable or disable checkpoint references. |
| [**enableBranchCommit**](BranchesApi.md#enablebranchcommit) | **POST** /branch/enableCommit | Enable or disable commit references. |
| [**enableBranchPromotion**](BranchesApi.md#enablebranchpromotion) | **POST** /branch/enablePromotion | Enable or disable promotion references. |
| [**enableBranchSave**](BranchesApi.md#enablebranchsave) | **POST** /branch/enableSave | Enable or disable save references. |
| [**enableBranchTag**](BranchesApi.md#enablebranchtag) | **POST** /branch/enableTag | Enable or disable tag references. |
| [**getBranch**](BranchesApi.md#getbranch) | **POST** /branch/get | Get a branch. |
| [**getBranchReference**](BranchesApi.md#getbranchreference) | **POST** /branch/getReference | Get a branch reference. |
| [**getParentBranch**](BranchesApi.md#getparentbranch) | **POST** /branch/getParentBranch | Get the parent branch. |
| [**listBranchCheckpoints**](BranchesApi.md#listbranchcheckpoints) | **POST** /branch/getCheckpoints | List branch checkpoints. |
| [**listBranchCommits**](BranchesApi.md#listbranchcommits) | **POST** /branch/getCommits | List branch commits. |
| [**listBranchPromotions**](BranchesApi.md#listbranchpromotions) | **POST** /branch/getPromotions | List branch promotions. |
| [**listBranchReferences**](BranchesApi.md#listbranchreferences) | **POST** /branch/getReferences | List branch references. |
| [**listBranchSaves**](BranchesApi.md#listbranchsaves) | **POST** /branch/getSaves | List branch saves. |
| [**listBranchTags**](BranchesApi.md#listbranchtags) | **POST** /branch/getTags | List branch tags. |
| [**promoteBranch**](BranchesApi.md#promotebranch) | **POST** /branch/promote | Promote the current branch content. |
| [**rebaseBranch**](BranchesApi.md#rebasebranch) | **POST** /branch/rebase | Rebase a branch. |
| [**saveBranch**](BranchesApi.md#savebranch) | **POST** /branch/save | Save the current branch content. |
| [**tagBranch**](BranchesApi.md#tagbranch) | **POST** /branch/tag | Tag the current branch content. |



## annotateBranch

> BranchAnnotationReturnValue annotateBranch(annotateParameters)

Annotate a branch reference.

Annotates lines from an existing server-known reference in a branch. The API reads server-stored reference content and does not save local workspace state.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { AnnotateBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // AnnotateParameters
    annotateParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","IncludeDeleted":false,"TargetReferenceId":"c8f9bac8-d489-46c7-917f-b36b7d9efa9a","Path":"src/App.fs","StartLine":1,"EndLine":1,"ReferenceTypes":["commit","save"],"MaxReferences":1000,"IncludeLineText":true},
  } satisfies AnnotateBranchRequest;

  try {
    const data = await api.annotateBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **annotateParameters** | [AnnotateParameters](AnnotateParameters.md) |  | |

### Return type

[**BranchAnnotationReturnValue**](BranchAnnotationReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## checkpointBranch

> BranchCommandReturnValue checkpointBranch(createReferenceParameters)

Checkpoint the current branch content.

Creates a checkpoint reference pointing to the current root directory version in the branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { CheckpointBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // CreateReferenceParameters
    createReferenceParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243","Blake3Hash":"9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d","Message":"Checkpoint release candidate."},
  } satisfies CheckpointBranchRequest;

  try {
    const data = await api.checkpointBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **createReferenceParameters** | [CreateReferenceParameters](CreateReferenceParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## commitBranch

> BranchCommandReturnValue commitBranch(createReferenceParameters)

Commit the current branch content.

Creates a commit reference pointing to the current root directory version in the branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { CommitBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // CreateReferenceParameters
    createReferenceParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243","Blake3Hash":"9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d","Message":"Commit release candidate."},
  } satisfies CommitBranchRequest;

  try {
    const data = await api.commitBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **createReferenceParameters** | [CreateReferenceParameters](CreateReferenceParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## createBranch

> BranchCommandReturnValue createBranch(createBranchParameters)

Create a branch.

Creates a branch with the specified name, based on the specified parent branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { CreateBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // CreateBranchParameters
    createBranchParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchName":"release-2026-06","ParentBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","InitialPermissions":["commit","checkpoint","save","tag"]},
  } satisfies CreateBranchRequest;

  try {
    const data = await api.createBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **createBranchParameters** | [CreateBranchParameters](CreateBranchParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## deleteBranch

> BranchCommandReturnValue deleteBranch(deleteBranchParameters)

Delete a branch.

Deletes the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DeleteBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // DeleteBranchParameters
    deleteBranchParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true,"Force":false,"DeleteReason":"Superseded by release branch.","ReassignChildBranches":true,"NewParentBranchId":"11111111-1111-1111-1111-111111111111"},
  } satisfies DeleteBranchRequest;

  try {
    const data = await api.deleteBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **deleteBranchParameters** | [DeleteBranchParameters](DeleteBranchParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## enableBranchCheckpoint

> BranchCommandReturnValue enableBranchCheckpoint(enableFeatureParameters)

Enable or disable checkpoint references.

Enables or disables checkpoint for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { EnableBranchCheckpointRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // EnableFeatureParameters
    enableFeatureParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true},
  } satisfies EnableBranchCheckpointRequest;

  try {
    const data = await api.enableBranchCheckpoint(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **enableFeatureParameters** | [EnableFeatureParameters](EnableFeatureParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## enableBranchCommit

> BranchCommandReturnValue enableBranchCommit(enableFeatureParameters)

Enable or disable commit references.

Enables or disables commit for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { EnableBranchCommitRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // EnableFeatureParameters
    enableFeatureParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true},
  } satisfies EnableBranchCommitRequest;

  try {
    const data = await api.enableBranchCommit(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **enableFeatureParameters** | [EnableFeatureParameters](EnableFeatureParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## enableBranchPromotion

> BranchCommandReturnValue enableBranchPromotion(enableFeatureParameters)

Enable or disable promotion references.

Enables or disables promotion for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { EnableBranchPromotionRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // EnableFeatureParameters
    enableFeatureParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true},
  } satisfies EnableBranchPromotionRequest;

  try {
    const data = await api.enableBranchPromotion(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **enableFeatureParameters** | [EnableFeatureParameters](EnableFeatureParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## enableBranchSave

> BranchCommandReturnValue enableBranchSave(enableFeatureParameters)

Enable or disable save references.

Enables or disables save for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { EnableBranchSaveRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // EnableFeatureParameters
    enableFeatureParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true},
  } satisfies EnableBranchSaveRequest;

  try {
    const data = await api.enableBranchSave(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **enableFeatureParameters** | [EnableFeatureParameters](EnableFeatureParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## enableBranchTag

> BranchCommandReturnValue enableBranchTag(enableFeatureParameters)

Enable or disable tag references.

Enables or disables tag for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { EnableBranchTagRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // EnableFeatureParameters
    enableFeatureParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Enabled":true},
  } satisfies EnableBranchTagRequest;

  try {
    const data = await api.enableBranchTag(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **enableFeatureParameters** | [EnableFeatureParameters](EnableFeatureParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getBranch

> BranchReturnValue getBranch(getBranchParameters)

Get a branch.

Gets the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // GetBranchParameters
    getBranchParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","IncludeDeleted":false},
  } satisfies GetBranchRequest;

  try {
    const data = await api.getBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getBranchParameters** | [GetBranchParameters](GetBranchParameters.md) |  | |

### Return type

[**BranchReturnValue**](BranchReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getBranchReference

> ReferenceReturnValue getBranchReference(getReferenceParameters)

Get a branch reference.

Gets the specified reference from the branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetBranchReferenceRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // GetReferenceParameters
    getReferenceParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","IncludeDeleted":false,"ReferenceId":"c8f9bac8-d489-46c7-917f-b36b7d9efa9a"},
  } satisfies GetBranchReferenceRequest;

  try {
    const data = await api.getBranchReference(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getReferenceParameters** | [GetReferenceParameters](GetReferenceParameters.md) |  | |

### Return type

[**ReferenceReturnValue**](ReferenceReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## getParentBranch

> BranchReturnValue getParentBranch(getBranchParameters)

Get the parent branch.

Gets the parent branch of the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { GetParentBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // GetBranchParameters
    getBranchParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","IncludeDeleted":false},
  } satisfies GetParentBranchRequest;

  try {
    const data = await api.getParentBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getBranchParameters** | [GetBranchParameters](GetBranchParameters.md) |  | |

### Return type

[**BranchReturnValue**](BranchReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## listBranchCheckpoints

> ReferenceListReturnValue listBranchCheckpoints(getReferencesParameters)

List branch checkpoints.

Gets the checkpoint references for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListBranchCheckpointsRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // GetReferencesParameters
    getReferencesParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50},
  } satisfies ListBranchCheckpointsRequest;

  try {
    const data = await api.listBranchCheckpoints(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getReferencesParameters** | [GetReferencesParameters](GetReferencesParameters.md) |  | |

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## listBranchCommits

> ReferenceListReturnValue listBranchCommits(getReferencesParameters)

List branch commits.

Gets the commit references for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListBranchCommitsRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // GetReferencesParameters
    getReferencesParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50},
  } satisfies ListBranchCommitsRequest;

  try {
    const data = await api.listBranchCommits(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getReferencesParameters** | [GetReferencesParameters](GetReferencesParameters.md) |  | |

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## listBranchPromotions

> ReferenceListReturnValue listBranchPromotions(getReferencesParameters)

List branch promotions.

Gets the promotion references for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListBranchPromotionsRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // GetReferencesParameters
    getReferencesParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50},
  } satisfies ListBranchPromotionsRequest;

  try {
    const data = await api.listBranchPromotions(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getReferencesParameters** | [GetReferencesParameters](GetReferencesParameters.md) |  | |

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## listBranchReferences

> ReferenceListReturnValue listBranchReferences(getReferencesParameters)

List branch references.

Gets the references for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListBranchReferencesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // GetReferencesParameters
    getReferencesParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50},
  } satisfies ListBranchReferencesRequest;

  try {
    const data = await api.listBranchReferences(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getReferencesParameters** | [GetReferencesParameters](GetReferencesParameters.md) |  | |

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## listBranchSaves

> ReferenceListReturnValue listBranchSaves(getReferencesParameters)

List branch saves.

Gets the save references for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListBranchSavesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // GetReferencesParameters
    getReferencesParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50},
  } satisfies ListBranchSavesRequest;

  try {
    const data = await api.listBranchSaves(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getReferencesParameters** | [GetReferencesParameters](GetReferencesParameters.md) |  | |

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## listBranchTags

> ReferenceListReturnValue listBranchTags(getReferencesParameters)

List branch tags.

Gets the tag references for the specified branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListBranchTagsRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // GetReferencesParameters
    getReferencesParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","FullSha":false,"MaxCount":50},
  } satisfies ListBranchTagsRequest;

  try {
    const data = await api.listBranchTags(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **getReferencesParameters** | [GetReferencesParameters](GetReferencesParameters.md) |  | |

### Return type

[**ReferenceListReturnValue**](ReferenceListReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## promoteBranch

> BranchCommandReturnValue promoteBranch(createReferenceParameters)

Promote the current branch content.

Creates a promotion reference in the parent of the specified branch, based on the current directory version.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { PromoteBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // CreateReferenceParameters
    createReferenceParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243","Blake3Hash":"9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d","Message":"Capture release candidate."},
  } satisfies PromoteBranchRequest;

  try {
    const data = await api.promoteBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **createReferenceParameters** | [CreateReferenceParameters](CreateReferenceParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## rebaseBranch

> BranchCommandReturnValue rebaseBranch(rebaseParameters)

Rebase a branch.

Rebases a branch on the specified reference from its parent branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { RebaseBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // RebaseParameters
    rebaseParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchName":"release-2026-06","ParentBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","InitialPermissions":["commit","checkpoint","save","tag"],"BasedOn":"c8f9bac8-d489-46c7-917f-b36b7d9efa9a"},
  } satisfies RebaseBranchRequest;

  try {
    const data = await api.rebaseBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **rebaseParameters** | [RebaseParameters](RebaseParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## saveBranch

> BranchCommandReturnValue saveBranch(createReferenceParameters)

Save the current branch content.

Creates a save reference pointing to the current root directory version in the branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { SaveBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // CreateReferenceParameters
    createReferenceParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243","Blake3Hash":"9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d","Message":"Save release candidate."},
  } satisfies SaveBranchRequest;

  try {
    const data = await api.saveBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **createReferenceParameters** | [CreateReferenceParameters](CreateReferenceParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)


## tagBranch

> BranchCommandReturnValue tagBranch(createReferenceParameters)

Tag the current branch content.

Creates a tag reference pointing to the current root directory version in the branch.

### Example

```ts
import {
  Configuration,
  BranchesApi,
} from '@grace-vcs/generated-openapi-probe';
import type { TagBranchRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new BranchesApi(config);

  const body = {
    // CreateReferenceParameters
    createReferenceParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","BranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","DirectoryVersionId":"33a4e36b-828f-4fae-9343-50b6560dc842","Sha256Hash":"805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243","Blake3Hash":"9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d","Message":"Tag release candidate."},
  } satisfies TagBranchRequest;

  try {
    const data = await api.tagBranch(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```

### Parameters


| Name | Type | Description  | Notes |
|------------- | ------------- | ------------- | -------------|
| **createReferenceParameters** | [CreateReferenceParameters](CreateReferenceParameters.md) |  | |

### Return type

[**BranchCommandReturnValue**](BranchCommandReturnValue.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: `application/json`
- **Accept**: `application/json`


### HTTP response details
| Status code | Description | Response headers |
|-------------|-------------|------------------|
| **200** | OK |  -  |
| **400** | Bad Request |  -  |
| **500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#api-endpoints) [[Back to Model list]](../README.md#models) [[Back to README]](../README.md)

