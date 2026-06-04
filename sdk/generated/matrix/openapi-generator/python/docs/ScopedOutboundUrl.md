# ScopedOutboundUrl


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**url** | **str** |  | [optional] 
**safety** | [**OutboundUrlSafety**](OutboundUrlSafety.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.scoped_outbound_url import ScopedOutboundUrl

# TODO update the JSON string below
json = "{}"
# create an instance of ScopedOutboundUrl from a JSON string
scoped_outbound_url_instance = ScopedOutboundUrl.from_json(json)
# print the JSON string representation of the object
print(ScopedOutboundUrl.to_json())

# convert the object into a dict
scoped_outbound_url_dict = scoped_outbound_url_instance.to_dict()
# create an instance of ScopedOutboundUrl from a dict
scoped_outbound_url_from_dict = ScopedOutboundUrl.from_dict(scoped_outbound_url_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


