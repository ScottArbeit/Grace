namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open NodaTime
open System

module User =

    [<Serializable>]
    type UserDto (userId, emailAddress, isPrivateDefault, isSuspended, isDeactivated, defaultTimeZone, updatedAt) =
        new() = UserDto(Guid.NewGuid(), String.Empty, false, false, false, TimeZoneInfo.Utc.Id, getCurrentInstant())
        member val public UserId : Guid = userId with get, set
        member val public EmailAddress: string = emailAddress with get, set
        member val public IsPrivateDefault: bool = isPrivateDefault with get, set
        member val public IsSuspended: bool = isSuspended with get, set
        member val public IsDeactivated: bool = isDeactivated with get, set
        member val public DefaultTimeZone: string = defaultTimeZone with get, set
        member val public UpdatedAt: Instant = updatedAt with get, set


    type User() = 
        let x = 0 
