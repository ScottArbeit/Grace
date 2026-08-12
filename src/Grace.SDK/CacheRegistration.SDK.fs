namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.CacheRegistration
open Grace.Types.CacheRegistration
open System.Threading

/// Provides the authenticated SDK facade for static Cache enrollment.
type CacheRegistration() =
    /// Enrolls one static Cache through the existing administrator-authorized server route.
    static member public Enroll(parameters: EnrollCacheParameters) =
        postServer<EnrollCacheParameters, CacheRegistrationResult> (parameters |> ensureCorrelationIdIsSet, "cache/enroll")

    /// Enrolls one static Cache while propagating CLI cancellation through the authenticated server request.
    static member public Enroll(parameters: EnrollCacheParameters, cancellationToken: CancellationToken) =
        postServerWithCancellation<EnrollCacheParameters, CacheRegistrationResult> (parameters |> ensureCorrelationIdIsSet, "cache/enroll", cancellationToken)
