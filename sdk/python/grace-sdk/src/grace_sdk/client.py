from ._generated.grace_raw_client_metadata import API_CONTRACT_VERSION, OPENAPI_PROJECTION_SHA256


class GraceClient:
    """Minimal facade placeholder for the extraction-compatible Python SDK package lane."""

    @property
    def api_contract_version(self) -> str:
        return API_CONTRACT_VERSION

    @property
    def openapi_projection_sha256(self) -> str:
        return OPENAPI_PROJECTION_SHA256
