# \WebhooksApi

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



## create_webhook_rule

> models::InlineObject4 create_webhook_rule(create_webhook_rule_parameters)
Create a webhook rule.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_webhook_rule_parameters** | [**CreateWebhookRuleParameters**](CreateWebhookRuleParameters.md) |  | [required] |

### Return type

[**models::InlineObject4**](inline_object_4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## delete_webhook_rule

> models::InlineObject4 delete_webhook_rule(webhook_rule_parameters)
Delete a webhook rule.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**webhook_rule_parameters** | [**WebhookRuleParameters**](WebhookRuleParameters.md) |  | [required] |

### Return type

[**models::InlineObject4**](inline_object_4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## disable_webhook_rule

> models::InlineObject4 disable_webhook_rule(webhook_rule_parameters)
Disable a webhook rule.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**webhook_rule_parameters** | [**WebhookRuleParameters**](WebhookRuleParameters.md) |  | [required] |

### Return type

[**models::InlineObject4**](inline_object_4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## enable_webhook_rule

> models::InlineObject4 enable_webhook_rule(webhook_rule_parameters)
Enable a webhook rule.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**webhook_rule_parameters** | [**WebhookRuleParameters**](WebhookRuleParameters.md) |  | [required] |

### Return type

[**models::InlineObject4**](inline_object_4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_webhook_deliveries

> models::InlineObject7 list_webhook_deliveries(list_webhook_deliveries_parameters)
List webhook deliveries.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**list_webhook_deliveries_parameters** | [**ListWebhookDeliveriesParameters**](ListWebhookDeliveriesParameters.md) |  | [required] |

### Return type

[**models::InlineObject7**](inline_object_7.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## list_webhook_rules

> models::InlineObject5 list_webhook_rules(list_webhook_rules_parameters)
List webhook rules.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**list_webhook_rules_parameters** | [**ListWebhookRulesParameters**](ListWebhookRulesParameters.md) |  | [required] |

### Return type

[**models::InlineObject5**](inline_object_5.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## show_webhook_delivery

> models::InlineObject6 show_webhook_delivery(webhook_delivery_parameters)
Show a webhook delivery.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**webhook_delivery_parameters** | [**WebhookDeliveryParameters**](WebhookDeliveryParameters.md) |  | [required] |

### Return type

[**models::InlineObject6**](inline_object_6.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## show_webhook_rule

> models::InlineObject4 show_webhook_rule(webhook_rule_parameters)
Show a webhook rule.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**webhook_rule_parameters** | [**WebhookRuleParameters**](WebhookRuleParameters.md) |  | [required] |

### Return type

[**models::InlineObject4**](inline_object_4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## test_webhook_rule

> models::InlineObject6 test_webhook_rule(test_webhook_rule_parameters)
Create a test webhook delivery.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**test_webhook_rule_parameters** | [**TestWebhookRuleParameters**](TestWebhookRuleParameters.md) |  | [required] |

### Return type

[**models::InlineObject6**](inline_object_6.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)


## update_webhook_rule

> models::InlineObject4 update_webhook_rule(create_webhook_rule_parameters)
Update a webhook rule.

### Parameters


Name | Type | Description  | Required | Notes
------------- | ------------- | ------------- | ------------- | -------------
**create_webhook_rule_parameters** | [**CreateWebhookRuleParameters**](CreateWebhookRuleParameters.md) |  | [required] |

### Return type

[**models::InlineObject4**](inline_object_4.md)

### Authorization

[bearerAuth](../README.md#bearerAuth)

### HTTP request headers

- **Content-Type**: application/json
- **Accept**: application/json

[[Back to top]](#) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to Model list]](../README.md#documentation-for-models) [[Back to README]](../README.md)

