namespace Grace.Actors

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open NodaTime
open System

/// Groups Orleans actor helpers for user keys, proxies, state, or workflow transitions.
module User =

    /// Wraps user dto records exchanged by actor queries or projections.
    [<Serializable>]
    type UserDto(userId, emailAddress, isPrivateDefault, isSuspended, isDeactivated, defaultTimeZone, updatedAt) =
        new() = UserDto(Guid.NewGuid(), String.Empty, false, false, false, TimeZoneInfo.Utc.Id, getCurrentInstant ())
        /// Stores the stable user id represented by this user actor state.
        member val public UserId: Guid = userId with get, set
        /// Stores the email address associated with the user actor.
        member val public EmailAddress: string = emailAddress with get, set
        /// Stores whether new user-owned resources default to private visibility.
        member val public IsPrivateDefault: bool = isPrivateDefault with get, set
        /// Stores whether the user is temporarily blocked from normal access.
        member val public IsSuspended: bool = isSuspended with get, set
        /// Stores whether the user account has been deactivated.
        member val public IsDeactivated: bool = isDeactivated with get, set
        /// Stores the user time zone used for date and reminder presentation.
        member val public DefaultTimeZone: string = defaultTimeZone with get, set
        /// Stores the timestamp of the latest user state update.
        member val public UpdatedAt: Instant = updatedAt with get, set


    /// Wraps user records exchanged by actor queries or projections.
    type User() =
        let x = 0
