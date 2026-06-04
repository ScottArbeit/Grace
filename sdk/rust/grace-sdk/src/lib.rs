mod generated;

pub struct GraceClient;

impl GraceClient {
    pub fn api_contract_version(&self) -> &'static str {
        generated::API_CONTRACT_VERSION
    }

    pub fn openapi_projection_sha256(&self) -> &'static str {
        generated::OPENAPI_PROJECTION_SHA256
    }

    pub fn openapi_version(&self) -> &'static str {
        generated::OPENAPI_VERSION
    }

    pub fn openapi_projection(&self) -> &'static str {
        generated::OPENAPI_PROJECTION
    }

    pub fn generator(&self) -> &'static str {
        generated::GENERATOR
    }

    pub fn generator_version(&self) -> &'static str {
        generated::GENERATOR_VERSION
    }
}

#[cfg(test)]
mod tests {
    use super::GraceClient;

    #[test]
    fn facade_exposes_contract_metadata() {
        let client = GraceClient;

        assert!(!client.api_contract_version().is_empty());
        assert_eq!(client.openapi_projection_sha256().len(), 64);
        assert_eq!(client.openapi_version(), "3.1.2");
        assert!(client.openapi_projection().ends_with(".yaml"));
        assert_eq!(client.generator(), "grace-sdk-harness");
        assert!(!client.generator_version().is_empty());
    }
}
