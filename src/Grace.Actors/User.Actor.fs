namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Identity
open Grace.Types.Types
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module User =

    let private createDefaultUser (userId: UserId) =
        { UserId = userId
          PrimaryDisplayName = String.Empty
          PrimaryEmail = None
          IsDisabled = false
          CreatedAt = getCurrentInstant ()
          ExternalIdentities = [] }

    type UserActor([<PersistentState(StateName.User, Constants.GraceActorStorage)>] state: IPersistentState<UserState>) =
        inherit Grain()

        let mutable userState: UserState = createDefaultUser String.Empty

        override this.OnActivateAsync(ct) =
            if state.RecordExists then
                userState <- state.State
            else
                let userId = this.GetPrimaryKeyString()
                userState <- createDefaultUser userId

            Task.CompletedTask

        member private this.WriteState(updatedState: UserState) =
            task {
                userState <- updatedState
                state.State <- updatedState
                do! state.WriteStateAsync()
                return updatedState
            }

        member private this.UpsertExternalIdentityInternal(externalIdentity: ExternalIdentity) =
            let normalizedIdentity =
                { externalIdentity with
                    Provider = externalIdentity.Provider.Trim()
                    Subject = externalIdentity.Subject.Trim()
                    LastSeenAt = getCurrentInstant () }

            let (existing, remaining) =
                userState.ExternalIdentities
                |> List.partition (fun identity ->
                    identity.Provider.Equals(normalizedIdentity.Provider, StringComparison.OrdinalIgnoreCase)
                    && identity.Subject.Equals(normalizedIdentity.Subject, StringComparison.OrdinalIgnoreCase)
                    && identity.TenantId = normalizedIdentity.TenantId)

            let mergedIdentity =
                match existing with
                | [] -> normalizedIdentity
                | current :: _ ->
                    { current with
                        Email =
                            if normalizedIdentity.Email.IsSome then
                                normalizedIdentity.Email
                            else
                                current.Email
                        DisplayName =
                            if normalizedIdentity.DisplayName.IsSome then
                                normalizedIdentity.DisplayName
                            else
                                current.DisplayName
                        LastSeenAt = normalizedIdentity.LastSeenAt
                        RawClaims = normalizedIdentity.RawClaims |> Option.orElse current.RawClaims }

            let primaryEmail =
                match userState.PrimaryEmail, mergedIdentity.Email with
                | None, Some _ -> mergedIdentity.Email
                | existingEmail, _ -> existingEmail

            let primaryDisplayName =
                if
                    String.IsNullOrWhiteSpace userState.PrimaryDisplayName
                    && mergedIdentity.DisplayName.IsSome
                then
                    mergedIdentity.DisplayName.Value
                else
                    userState.PrimaryDisplayName

            { userState with PrimaryEmail = primaryEmail; PrimaryDisplayName = primaryDisplayName; ExternalIdentities = mergedIdentity :: remaining }

        interface IUserActor with
            member this.Get correlationId = Task.FromResult userState

            member this.UpsertExternalIdentity externalIdentity correlationId =
                task {
                    let updatedState = this.UpsertExternalIdentityInternal externalIdentity
                    let! persisted = this.WriteState updatedState
                    return persisted
                }

            member this.Disable correlationId =
                task {
                    if not userState.IsDisabled then
                        let! _ = this.WriteState { userState with IsDisabled = true }
                        return ()
                }

            member this.Enable correlationId =
                task {
                    if userState.IsDisabled then
                        let! _ = this.WriteState { userState with IsDisabled = false }
                        return ()
                }
