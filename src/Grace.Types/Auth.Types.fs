namespace Grace.Types

open Orleans

module Auth =
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
