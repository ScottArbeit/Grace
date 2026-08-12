namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.CacheRegistration
open System
open System.Collections.Generic

/// Contains private SDK parameter construction for administrator-initiated Cache enrollment.
module CacheRegistration =
    /// Carries the exact public enrollment request through the normal authenticated SDK transport.
    type EnrollCacheParameters() =
        inherit CommonParameters()
        member val public Class = nameof CacheEnrollmentRequest with get, set
        member val public DisplayName = String.Empty with get, set
        member val public BoundaryKind = CacheBoundaryKind.Owner with get, set
        member val public OwnerId = Guid.Empty with get, set
        member val public OrganizationId: Guid option = None with get, set
        member val public RepositoryScopes = List<CacheRepositoryScope>() with get, set
        member val public PublicKey = Unchecked.defaultof<CacheIdentityPublicKey> with get, set
        member val public Endpoint = String.Empty with get, set
        member val public AllowHttpEndpoint = false with get, set
        member val public SoftwareVersion = String.Empty with get, set
        member val public ProtocolVersion = String.Empty with get, set
        member val public PrefetchSupported = false with get, set
