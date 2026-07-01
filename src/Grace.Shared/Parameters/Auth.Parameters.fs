namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.PersonalAccessToken
open Orleans
open System

/// Contains auth helpers.
module Auth =
    /// Represents auth parameters.
    [<GenerateSerializer>]
    type AuthParameters() =
        inherit CommonParameters()

    /// Represents create personal access token parameters.
    type CreatePersonalAccessTokenParameters() =
        inherit AuthParameters()
        member val public TokenName = String.Empty with get, set
        member val public ExpiresInSeconds = 0L with get, set
        member val public NoExpiry = false with get, set

    /// Represents list personal access tokens parameters.
    type ListPersonalAccessTokensParameters() =
        inherit AuthParameters()
        member val public IncludeRevoked = false with get, set
        member val public IncludeExpired = false with get, set

    /// Represents revoke personal access token parameters.
    type RevokePersonalAccessTokenParameters() =
        inherit AuthParameters()
        member val public TokenId: PersonalAccessTokenId = Guid.Empty with get, set
