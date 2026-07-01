namespace Grace.Types

open Orleans

/// Contains auth helpers.
module Auth =
    /// Represents oidc client config.
    [<GenerateSerializer>]
    type OidcClientConfig =
        {
            [<Id 0u>]
            Authority: string
            [<Id 1u>]
            Audience: string
            [<Id 2u>]
            CliClientId: string
        }
