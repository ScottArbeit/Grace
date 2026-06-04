# WebhooksApi

All URIs are relative to *http://localhost:5000*

| Method | HTTP request | Description |
|------------- | ------------- | -------------|
| [**createWebhookRule**](WebhooksApi.md#createwebhookrule) | **POST** /webhook/rule/create | Create a webhook rule. |
| [**deleteWebhookRule**](WebhooksApi.md#deletewebhookrule) | **POST** /webhook/rule/delete | Delete a webhook rule. |
| [**disableWebhookRule**](WebhooksApi.md#disablewebhookrule) | **POST** /webhook/rule/disable | Disable a webhook rule. |
| [**enableWebhookRule**](WebhooksApi.md#enablewebhookrule) | **POST** /webhook/rule/enable | Enable a webhook rule. |
| [**listWebhookDeliveries**](WebhooksApi.md#listwebhookdeliveries) | **POST** /webhook/delivery/list | List webhook deliveries. |
| [**listWebhookRules**](WebhooksApi.md#listwebhookrules) | **POST** /webhook/rule/list | List webhook rules. |
| [**showWebhookDelivery**](WebhooksApi.md#showwebhookdelivery) | **POST** /webhook/delivery/show | Show a webhook delivery. |
| [**showWebhookRule**](WebhooksApi.md#showwebhookrule) | **POST** /webhook/rule/show | Show a webhook rule. |
| [**testWebhookRule**](WebhooksApi.md#testwebhookrule) | **POST** /webhook/rule/test | Create a test webhook delivery. |
| [**updateWebhookRule**](WebhooksApi.md#updatewebhookrule) | **POST** /webhook/rule/update | Update a webhook rule. |



## createWebhookRule

> InlineObject4 createWebhookRule(createWebhookRuleParameters)

Create a webhook rule.

### Example

```ts
import {
  Configuration,
  WebhooksApi,
} from '@grace-vcs/generated-openapi-probe';
import type { CreateWebhookRuleRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WebhooksApi(config);

  const body = {
    // CreateWebhookRuleParameters
    createWebhookRuleParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","TargetBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Name":"Promotion applied webhook","EventName":"promotion-set.applied","EventVersion":1,"Url":"https://hooks.example.net/grace","UrlSafety":"PublicHttps","AcknowledgeUnsafeLocalDevelopment":false,"SigningSecretVersion":"secret-v1","MaxAttempts":8,"InitialDelaySeconds":30,"MaxDelaySeconds":3600},
  } satisfies CreateWebhookRuleRequest;

  try {
    const data = await api.createWebhookRule(body);
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
| **createWebhookRuleParameters** | [CreateWebhookRuleParameters](CreateWebhookRuleParameters.md) |  | |

### Return type

[**InlineObject4**](InlineObject4.md)

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


## deleteWebhookRule

> InlineObject4 deleteWebhookRule(webhookRuleParameters)

Delete a webhook rule.

### Example

```ts
import {
  Configuration,
  WebhooksApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DeleteWebhookRuleRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WebhooksApi(config);

  const body = {
    // WebhookRuleParameters
    webhookRuleParameters: ...,
  } satisfies DeleteWebhookRuleRequest;

  try {
    const data = await api.deleteWebhookRule(body);
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
| **webhookRuleParameters** | [WebhookRuleParameters](WebhookRuleParameters.md) |  | |

### Return type

[**InlineObject4**](InlineObject4.md)

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


## disableWebhookRule

> InlineObject4 disableWebhookRule(webhookRuleParameters)

Disable a webhook rule.

### Example

```ts
import {
  Configuration,
  WebhooksApi,
} from '@grace-vcs/generated-openapi-probe';
import type { DisableWebhookRuleRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WebhooksApi(config);

  const body = {
    // WebhookRuleParameters
    webhookRuleParameters: ...,
  } satisfies DisableWebhookRuleRequest;

  try {
    const data = await api.disableWebhookRule(body);
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
| **webhookRuleParameters** | [WebhookRuleParameters](WebhookRuleParameters.md) |  | |

### Return type

[**InlineObject4**](InlineObject4.md)

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


## enableWebhookRule

> InlineObject4 enableWebhookRule(webhookRuleParameters)

Enable a webhook rule.

### Example

```ts
import {
  Configuration,
  WebhooksApi,
} from '@grace-vcs/generated-openapi-probe';
import type { EnableWebhookRuleRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WebhooksApi(config);

  const body = {
    // WebhookRuleParameters
    webhookRuleParameters: ...,
  } satisfies EnableWebhookRuleRequest;

  try {
    const data = await api.enableWebhookRule(body);
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
| **webhookRuleParameters** | [WebhookRuleParameters](WebhookRuleParameters.md) |  | |

### Return type

[**InlineObject4**](InlineObject4.md)

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


## listWebhookDeliveries

> InlineObject7 listWebhookDeliveries(listWebhookDeliveriesParameters)

List webhook deliveries.

### Example

```ts
import {
  Configuration,
  WebhooksApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListWebhookDeliveriesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WebhooksApi(config);

  const body = {
    // ListWebhookDeliveriesParameters
    listWebhookDeliveriesParameters: ...,
  } satisfies ListWebhookDeliveriesRequest;

  try {
    const data = await api.listWebhookDeliveries(body);
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
| **listWebhookDeliveriesParameters** | [ListWebhookDeliveriesParameters](ListWebhookDeliveriesParameters.md) |  | |

### Return type

[**InlineObject7**](InlineObject7.md)

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


## listWebhookRules

> InlineObject5 listWebhookRules(listWebhookRulesParameters)

List webhook rules.

### Example

```ts
import {
  Configuration,
  WebhooksApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ListWebhookRulesRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WebhooksApi(config);

  const body = {
    // ListWebhookRulesParameters
    listWebhookRulesParameters: ...,
  } satisfies ListWebhookRulesRequest;

  try {
    const data = await api.listWebhookRules(body);
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
| **listWebhookRulesParameters** | [ListWebhookRulesParameters](ListWebhookRulesParameters.md) |  | |

### Return type

[**InlineObject5**](InlineObject5.md)

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


## showWebhookDelivery

> InlineObject6 showWebhookDelivery(webhookDeliveryParameters)

Show a webhook delivery.

### Example

```ts
import {
  Configuration,
  WebhooksApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ShowWebhookDeliveryRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WebhooksApi(config);

  const body = {
    // WebhookDeliveryParameters
    webhookDeliveryParameters: ...,
  } satisfies ShowWebhookDeliveryRequest;

  try {
    const data = await api.showWebhookDelivery(body);
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
| **webhookDeliveryParameters** | [WebhookDeliveryParameters](WebhookDeliveryParameters.md) |  | |

### Return type

[**InlineObject6**](InlineObject6.md)

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


## showWebhookRule

> InlineObject4 showWebhookRule(webhookRuleParameters)

Show a webhook rule.

### Example

```ts
import {
  Configuration,
  WebhooksApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ShowWebhookRuleRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WebhooksApi(config);

  const body = {
    // WebhookRuleParameters
    webhookRuleParameters: ...,
  } satisfies ShowWebhookRuleRequest;

  try {
    const data = await api.showWebhookRule(body);
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
| **webhookRuleParameters** | [WebhookRuleParameters](WebhookRuleParameters.md) |  | |

### Return type

[**InlineObject4**](InlineObject4.md)

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


## testWebhookRule

> InlineObject6 testWebhookRule(testWebhookRuleParameters)

Create a test webhook delivery.

### Example

```ts
import {
  Configuration,
  WebhooksApi,
} from '@grace-vcs/generated-openapi-probe';
import type { TestWebhookRuleRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WebhooksApi(config);

  const body = {
    // TestWebhookRuleParameters
    testWebhookRuleParameters: {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","TargetBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","WebhookRuleId":"73c9c3c8-5159-4e31-9280-e1b95e20db77","DedupeKey":"manual-test-20260604-001"},
  } satisfies TestWebhookRuleRequest;

  try {
    const data = await api.testWebhookRule(body);
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
| **testWebhookRuleParameters** | [TestWebhookRuleParameters](TestWebhookRuleParameters.md) |  | |

### Return type

[**InlineObject6**](InlineObject6.md)

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


## updateWebhookRule

> InlineObject4 updateWebhookRule(createWebhookRuleParameters)

Update a webhook rule.

### Example

```ts
import {
  Configuration,
  WebhooksApi,
} from '@grace-vcs/generated-openapi-probe';
import type { UpdateWebhookRuleRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new WebhooksApi(config);

  const body = {
    // CreateWebhookRuleParameters
    createWebhookRuleParameters: ...,
  } satisfies UpdateWebhookRuleRequest;

  try {
    const data = await api.updateWebhookRule(body);
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
| **createWebhookRuleParameters** | [CreateWebhookRuleParameters](CreateWebhookRuleParameters.md) |  | |

### Return type

[**InlineObject4**](InlineObject4.md)

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

