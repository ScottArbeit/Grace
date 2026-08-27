# P256PublicJwk


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**kty** | **str** |  | 
**crv** | **str** |  | 
**x** | **str** |  | 
**y** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.p256_public_jwk import P256PublicJwk

# TODO update the JSON string below
json = "{}"
# create an instance of P256PublicJwk from a JSON string
p256_public_jwk_instance = P256PublicJwk.from_json(json)
# print the JSON string representation of the object
print(P256PublicJwk.to_json())

# convert the object into a dict
p256_public_jwk_dict = p256_public_jwk_instance.to_dict()
# create an instance of P256PublicJwk from a dict
p256_public_jwk_from_dict = P256PublicJwk.from_dict(p256_public_jwk_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


