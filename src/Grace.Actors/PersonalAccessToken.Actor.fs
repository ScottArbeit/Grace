namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.PersonalAccessToken
open Grace.Types.Types
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Security.Cryptography
open System.Threading.Tasks

module PersonalAccessToken =

    [<GenerateSerializer>]
    type PersonalAccessTokenRecord =
        { TokenId: PersonalAccessTokenId
          Name: string
          CreatedAt: Instant
          ExpiresAt: Instant option
          LastUsedAt: Instant option
          RevokedAt: Instant option
          Salt: byte array
          Hash: byte array
          Claims: string list
          GroupIds: string list }

    [<GenerateSerializer>]
    type PersonalAccessTokenState = { Tokens: PersonalAccessTokenRecord list }

    module PersonalAccessTokenState =
        let Empty = { Tokens = [] }

    type PersonalAccessTokenActor
        ([<PersistentState(StateName.PersonalAccessToken, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<PersonalAccessTokenState>) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("PersonalAccessToken.Actor")

        let mutable tokenState = PersonalAccessTokenState.Empty

        override this.OnActivateAsync(ct) =
            tokenState <- if state.RecordExists then state.State else PersonalAccessTokenState.Empty
            Task.CompletedTask

        member private this.SaveState() =
            task {
                state.State <- tokenState

                if tokenState.Tokens |> List.isEmpty then
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> state.ClearStateAsync())
                else
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> state.WriteStateAsync())
            }

        member private _.Summarize(record: PersonalAccessTokenRecord) =
            { TokenId = record.TokenId
              Name = record.Name
              CreatedAt = record.CreatedAt
              ExpiresAt = record.ExpiresAt
              LastUsedAt = record.LastUsedAt
              RevokedAt = record.RevokedAt }

        member private _.ComputeHash (salt: byte array) (secret: byte array) =
            let combined = Array.zeroCreate<byte> (salt.Length + secret.Length)
            Array.Copy(salt, 0, combined, 0, salt.Length)
            Array.Copy(secret, 0, combined, salt.Length, secret.Length)
            SHA256.HashData combined

        member private this.CreateToken
            (name: string)
            (claims: string list)
            (groupIds: string list)
            (expiresAt: Instant option)
            (now: Instant)
            (correlationId: CorrelationId)
            =
            task {
                let existingName =
                    tokenState.Tokens
                    |> List.exists (fun token -> token.Name.Equals(name, StringComparison.OrdinalIgnoreCase))

                if existingName then
                    return Error(GraceError.Create "Token name already exists." correlationId)
                else
                    let tokenId = Guid.NewGuid()
                    let secret = Array.zeroCreate<byte> 32
                    let salt = Array.zeroCreate<byte> 32
                    RandomNumberGenerator.Fill(secret)
                    RandomNumberGenerator.Fill(salt)

                    let hash = this.ComputeHash salt secret

                    let record =
                        { TokenId = tokenId
                          Name = name
                          CreatedAt = now
                          ExpiresAt = expiresAt
                          LastUsedAt = None
                          RevokedAt = None
                          Salt = salt
                          Hash = hash
                          Claims = claims
                          GroupIds = groupIds }

                    tokenState <- { tokenState with Tokens = record :: tokenState.Tokens }
                    do! this.SaveState()

                    let userId = this.GetPrimaryKeyString()
                    let token = formatToken userId tokenId secret
                    let summary = this.Summarize record
                    let result = { Token = token; Summary = summary }

                    log.LogInformation(
                        "{CurrentInstant}: Created PAT for user {UserId}. CorrelationId: {CorrelationId}; TokenId: {TokenId}.",
                        getCurrentInstantExtended (),
                        userId,
                        correlationId,
                        tokenId
                    )

                    return Ok result
            }

        member private this.ListTokens (includeRevoked: bool) (includeExpired: bool) (now: Instant) (_correlationId: CorrelationId) =
            task {
                let isExpired (token: PersonalAccessTokenRecord) =
                    match token.ExpiresAt with
                    | None -> false
                    | Some expiresAt -> expiresAt <= now

                let filtered =
                    tokenState.Tokens
                    |> List.filter (fun token ->
                        let revokedOk = includeRevoked || token.RevokedAt.IsNone
                        let expiredOk = includeExpired || not (isExpired token)
                        revokedOk && expiredOk)
                    |> List.sortByDescending (fun token -> token.CreatedAt)
                    |> List.map this.Summarize

                return filtered
            }

        member private this.RevokeToken (tokenId: PersonalAccessTokenId) (now: Instant) (correlationId: CorrelationId) =
            task {
                match tokenState.Tokens |> List.tryFind (fun token -> token.TokenId = tokenId) with
                | None -> return Error(GraceError.Create "Token not found." correlationId)
                | Some record ->
                    let updated = { record with RevokedAt = Some now }
                    let remaining = tokenState.Tokens |> List.filter (fun token -> token.TokenId <> tokenId)
                    tokenState <- { tokenState with Tokens = updated :: remaining }
                    do! this.SaveState()
                    return Ok(this.Summarize updated)
            }

        member private this.ValidateToken (tokenId: PersonalAccessTokenId) (secret: byte array) (now: Instant) (_correlationId: CorrelationId) =
            task {
                let isExpired (token: PersonalAccessTokenRecord) =
                    match token.ExpiresAt with
                    | None -> false
                    | Some expiresAt -> expiresAt <= now

                match tokenState.Tokens |> List.tryFind (fun token -> token.TokenId = tokenId) with
                | None -> return None
                | Some record ->
                    if record.RevokedAt.IsSome || isExpired record then
                        return None
                    else
                        let computed = this.ComputeHash record.Salt secret

                        if CryptographicOperations.FixedTimeEquals(computed, record.Hash) then
                            let updated = { record with LastUsedAt = Some now }
                            let remaining = tokenState.Tokens |> List.filter (fun token -> token.TokenId <> tokenId)

                            tokenState <- { tokenState with Tokens = updated :: remaining }
                            do! this.SaveState()

                            let result = { TokenId = record.TokenId; UserId = this.GetPrimaryKeyString(); Claims = record.Claims; GroupIds = record.GroupIds }

                            return Some result
                        else
                            return None
            }

        interface IPersonalAccessTokenActor with
            member this.CreateToken name claims groupIds expiresAt now correlationId = this.CreateToken name claims groupIds expiresAt now correlationId

            member this.ListTokens includeRevoked includeExpired now correlationId = this.ListTokens includeRevoked includeExpired now correlationId

            member this.RevokeToken tokenId now correlationId = this.RevokeToken tokenId now correlationId

            member this.ValidateToken tokenId secret now correlationId = this.ValidateToken tokenId secret now correlationId
