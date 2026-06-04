# grace_generated_openapi_probe.WebhooksApi

All URIs are relative to *http://localhost:5000*

Method | HTTP request | Description
------------- | ------------- | -------------
[**create_webhook_rule**](WebhooksApi.md#create_webhook_rule) | **POST** /webhook/rule/create | Create a webhook rule.
[**delete_webhook_rule**](WebhooksApi.md#delete_webhook_rule) | **POST** /webhook/rule/delete | Delete a webhook rule.
[**disable_webhook_rule**](WebhooksApi.md#disable_webhook_rule) | **POST** /webhook/rule/disable | Disable a webhook rule.
[**enable_webhook_rule**](WebhooksApi.md#enable_webhook_rule) | **POST** /webhook/rule/enable | Enable a webhook rule.
[**list_webhook_deliveries**](WebhooksApi.md#list_webhook_deliveries) | **POST** /webhook/delivery/list | List webhook deliveries.
[**list_webhook_rules**](WebhooksApi.md#list_webhook_rules) | **POST** /webhook/rule/list | List webhook rules.
[**show_webhook_delivery**](WebhooksApi.md#show_webhook_delivery) | **POST** /webhook/delivery/show | Show a webhook delivery.
[**show_webhook_rule**](WebhooksApi.md#show_webhook_rule) | **POST** /webhook/rule/show | Show a webhook rule.
[**test_webhook_rule**](WebhooksApi.md#test_webhook_rule) | **POST** /webhook/rule/test | Create a test webhook delivery.
[**update_webhook_rule**](WebhooksApi.md#update_webhook_rule) | **POST** /webhook/rule/update | Update a webhook rule.


# **create_webhook_rule**
> InlineObject4 create_webhook_rule(create_webhook_rule_parameters)

Create a webhook rule.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.create_webhook_rule_parameters import CreateWebhookRuleParameters
from grace_generated_openapi_probe.models.inline_object4 import InlineObject4
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.WebhooksApi(api_client)
    create_webhook_rule_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","TargetBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","Name":"Promotion applied webhook","EventName":"promotion-set.applied","EventVersion":1,"Url":"https://hooks.example.net/grace","UrlSafety":"PublicHttps","AcknowledgeUnsafeLocalDevelopment":false,"SigningSecretVersion":"secret-v1","MaxAttempts":8,"InitialDelaySeconds":30,"MaxDelaySeconds":3600} # CreateWebhookRuleParameters | 

    try:
        # Create a webhook rule.
        api_response = api_instance.create_webhook_rule(create_webhook_rule_parameters)
        print("The response of WebhooksApi->create_webhook_rule:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WebhooksApi->create_webhook_rule: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_webhook_rule_parameters** | [**CreateWebhookRuleParameters**](CreateWebhookRuleParameters.md)|  | 

### Return type

[**InlineObject4**](InlineObject4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **delete_webhook_rule**
> InlineObject4 delete_webhook_rule(webhook_rule_parameters)

Delete a webhook rule.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object4 import InlineObject4
from grace_generated_openapi_probe.models.webhook_rule_parameters import WebhookRuleParameters
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.WebhooksApi(api_client)
    webhook_rule_parameters = grace_generated_openapi_probe.WebhookRuleParameters() # WebhookRuleParameters | 

    try:
        # Delete a webhook rule.
        api_response = api_instance.delete_webhook_rule(webhook_rule_parameters)
        print("The response of WebhooksApi->delete_webhook_rule:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WebhooksApi->delete_webhook_rule: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **webhook_rule_parameters** | [**WebhookRuleParameters**](WebhookRuleParameters.md)|  | 

### Return type

[**InlineObject4**](InlineObject4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **disable_webhook_rule**
> InlineObject4 disable_webhook_rule(webhook_rule_parameters)

Disable a webhook rule.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object4 import InlineObject4
from grace_generated_openapi_probe.models.webhook_rule_parameters import WebhookRuleParameters
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.WebhooksApi(api_client)
    webhook_rule_parameters = grace_generated_openapi_probe.WebhookRuleParameters() # WebhookRuleParameters | 

    try:
        # Disable a webhook rule.
        api_response = api_instance.disable_webhook_rule(webhook_rule_parameters)
        print("The response of WebhooksApi->disable_webhook_rule:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WebhooksApi->disable_webhook_rule: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **webhook_rule_parameters** | [**WebhookRuleParameters**](WebhookRuleParameters.md)|  | 

### Return type

[**InlineObject4**](InlineObject4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **enable_webhook_rule**
> InlineObject4 enable_webhook_rule(webhook_rule_parameters)

Enable a webhook rule.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object4 import InlineObject4
from grace_generated_openapi_probe.models.webhook_rule_parameters import WebhookRuleParameters
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.WebhooksApi(api_client)
    webhook_rule_parameters = grace_generated_openapi_probe.WebhookRuleParameters() # WebhookRuleParameters | 

    try:
        # Enable a webhook rule.
        api_response = api_instance.enable_webhook_rule(webhook_rule_parameters)
        print("The response of WebhooksApi->enable_webhook_rule:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WebhooksApi->enable_webhook_rule: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **webhook_rule_parameters** | [**WebhookRuleParameters**](WebhookRuleParameters.md)|  | 

### Return type

[**InlineObject4**](InlineObject4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **list_webhook_deliveries**
> InlineObject7 list_webhook_deliveries(list_webhook_deliveries_parameters)

List webhook deliveries.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object7 import InlineObject7
from grace_generated_openapi_probe.models.list_webhook_deliveries_parameters import ListWebhookDeliveriesParameters
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.WebhooksApi(api_client)
    list_webhook_deliveries_parameters = grace_generated_openapi_probe.ListWebhookDeliveriesParameters() # ListWebhookDeliveriesParameters | 

    try:
        # List webhook deliveries.
        api_response = api_instance.list_webhook_deliveries(list_webhook_deliveries_parameters)
        print("The response of WebhooksApi->list_webhook_deliveries:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WebhooksApi->list_webhook_deliveries: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **list_webhook_deliveries_parameters** | [**ListWebhookDeliveriesParameters**](ListWebhookDeliveriesParameters.md)|  | 

### Return type

[**InlineObject7**](InlineObject7.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **list_webhook_rules**
> InlineObject5 list_webhook_rules(list_webhook_rules_parameters)

List webhook rules.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object5 import InlineObject5
from grace_generated_openapi_probe.models.list_webhook_rules_parameters import ListWebhookRulesParameters
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.WebhooksApi(api_client)
    list_webhook_rules_parameters = grace_generated_openapi_probe.ListWebhookRulesParameters() # ListWebhookRulesParameters | 

    try:
        # List webhook rules.
        api_response = api_instance.list_webhook_rules(list_webhook_rules_parameters)
        print("The response of WebhooksApi->list_webhook_rules:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WebhooksApi->list_webhook_rules: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **list_webhook_rules_parameters** | [**ListWebhookRulesParameters**](ListWebhookRulesParameters.md)|  | 

### Return type

[**InlineObject5**](InlineObject5.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **show_webhook_delivery**
> InlineObject6 show_webhook_delivery(webhook_delivery_parameters)

Show a webhook delivery.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object6 import InlineObject6
from grace_generated_openapi_probe.models.webhook_delivery_parameters import WebhookDeliveryParameters
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.WebhooksApi(api_client)
    webhook_delivery_parameters = grace_generated_openapi_probe.WebhookDeliveryParameters() # WebhookDeliveryParameters | 

    try:
        # Show a webhook delivery.
        api_response = api_instance.show_webhook_delivery(webhook_delivery_parameters)
        print("The response of WebhooksApi->show_webhook_delivery:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WebhooksApi->show_webhook_delivery: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **webhook_delivery_parameters** | [**WebhookDeliveryParameters**](WebhookDeliveryParameters.md)|  | 

### Return type

[**InlineObject6**](InlineObject6.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **show_webhook_rule**
> InlineObject4 show_webhook_rule(webhook_rule_parameters)

Show a webhook rule.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object4 import InlineObject4
from grace_generated_openapi_probe.models.webhook_rule_parameters import WebhookRuleParameters
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.WebhooksApi(api_client)
    webhook_rule_parameters = grace_generated_openapi_probe.WebhookRuleParameters() # WebhookRuleParameters | 

    try:
        # Show a webhook rule.
        api_response = api_instance.show_webhook_rule(webhook_rule_parameters)
        print("The response of WebhooksApi->show_webhook_rule:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WebhooksApi->show_webhook_rule: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **webhook_rule_parameters** | [**WebhookRuleParameters**](WebhookRuleParameters.md)|  | 

### Return type

[**InlineObject4**](InlineObject4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **test_webhook_rule**
> InlineObject6 test_webhook_rule(test_webhook_rule_parameters)

Create a test webhook delivery.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.inline_object6 import InlineObject6
from grace_generated_openapi_probe.models.test_webhook_rule_parameters import TestWebhookRuleParameters
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.WebhooksApi(api_client)
    test_webhook_rule_parameters = {"CorrelationId":"cli-20260604T181500Z-0001","Principal":"user:alice","OwnerId":"9dd5f81f-dc43-4839-9173-85d09394f30f","OrganizationId":"e35d64a9-b990-44f5-bf02-32ad7d15630c","RepositoryId":"ab6f35ef-6e01-440b-8f9b-c343a5272095","TargetBranchId":"de7bf47d-23ae-4599-af68-68a317ea390d","WebhookRuleId":"73c9c3c8-5159-4e31-9280-e1b95e20db77","DedupeKey":"manual-test-20260604-001"} # TestWebhookRuleParameters | 

    try:
        # Create a test webhook delivery.
        api_response = api_instance.test_webhook_rule(test_webhook_rule_parameters)
        print("The response of WebhooksApi->test_webhook_rule:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WebhooksApi->test_webhook_rule: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **test_webhook_rule_parameters** | [**TestWebhookRuleParameters**](TestWebhookRuleParameters.md)|  | 

### Return type

[**InlineObject6**](InlineObject6.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

# **update_webhook_rule**
> InlineObject4 update_webhook_rule(create_webhook_rule_parameters)

Update a webhook rule.

### Example

* Bearer (JWT) Authentication (bearerAuth):

```python
import grace_generated_openapi_probe
from grace_generated_openapi_probe.models.create_webhook_rule_parameters import CreateWebhookRuleParameters
from grace_generated_openapi_probe.models.inline_object4 import InlineObject4
from grace_generated_openapi_probe.rest import ApiException
from pprint import pprint

# Defining the host is optional and defaults to http://localhost:5000
# See configuration.py for a list of all supported configuration parameters.
configuration = grace_generated_openapi_probe.Configuration(
    host = "http://localhost:5000"
)

# The client must configure the authentication and authorization parameters
# in accordance with the API server security policy.
# Examples for each auth method are provided below, use the example that
# satisfies your auth use case.

# Configure Bearer authorization (JWT): bearerAuth
configuration = grace_generated_openapi_probe.Configuration(
    access_token = os.environ["BEARER_TOKEN"]
)

# Enter a context with an instance of the API client
with grace_generated_openapi_probe.ApiClient(configuration) as api_client:
    # Create an instance of the API class
    api_instance = grace_generated_openapi_probe.WebhooksApi(api_client)
    create_webhook_rule_parameters = grace_generated_openapi_probe.CreateWebhookRuleParameters() # CreateWebhookRuleParameters | 

    try:
        # Update a webhook rule.
        api_response = api_instance.update_webhook_rule(create_webhook_rule_parameters)
        print("The response of WebhooksApi->update_webhook_rule:\n")
        pprint(api_response)
    except Exception as e:
        print("Exception when calling WebhooksApi->update_webhook_rule: %s\n" % e)
```



### Parameters


Name | Type | Description  | Notes
------------- | ------------- | ------------- | -------------
 **create_webhook_rule_parameters** | [**CreateWebhookRuleParameters**](CreateWebhookRuleParameters.md)|  | 

### Return type

[**InlineObject4**](InlineObject4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

 - **Content-Type**: application/json
 - **Accept**: application/json

### HTTP response details

| Status code | Description | Response headers |
|-------------|-------------|------------------|
**200** | OK |  -  |
**400** | Bad Request |  -  |
**500** | Internal Server Error |  -  |

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

