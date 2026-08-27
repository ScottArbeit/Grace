namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared
open Grace.Shared.Parameters.SynchronizedContent
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.SynchronizedContent
open System
open System.Net.Http
open System.Threading.Tasks

/// SDK entry point for the complete authorized remote synchronized-content contract.
type SynchronizedContent() =

    /// Reads the current persisted synchronized-root configuration.
    static member public GetRoots(parameters: GetSynchronizedRootConfigurationParameters) =
        postServer<GetSynchronizedRootConfigurationParameters, SynchronizedRootConfigurationDto> (parameters |> ensureCorrelationIdIsSet, "sync/roots/get")

    /// Lists normalized synchronized roots in deterministic path order.
    static member public ListRoots(parameters: ListSynchronizedRootsParameters) =
        postServer<ListSynchronizedRootsParameters, SynchronizedRootConfigurationDto> (parameters |> ensureCorrelationIdIsSet, "sync/roots/list")

    /// Adds one synchronized root under an exact current configuration version.
    static member public AddRoot(parameters: AddSynchronizedRootParameters) =
        postServer<AddSynchronizedRootParameters, SynchronizedRootMutationResultDto> (parameters |> ensureCorrelationIdIsSet, "sync/roots/add")

    /// Removes one empty synchronized root under an exact current configuration version.
    static member public RemoveRoot(parameters: RemoveSynchronizedRootParameters) =
        postServer<RemoveSynchronizedRootParameters, SynchronizedRootMutationResultDto> (parameters |> ensureCorrelationIdIsSet, "sync/roots/remove")

    /// Starts one immutable baseline sequence from the latest published synchronized state.
    static member public StartBootstrap(parameters: StartSynchronizedBootstrapParameters) =
        postServer<StartSynchronizedBootstrapParameters, SynchronizedBootstrapPageDto> (parameters |> ensureCorrelationIdIsSet, "sync/bootstrap/start")

    /// Continues one immutable baseline sequence using its protected page token.
    static member public ContinueBootstrap(parameters: ContinueSynchronizedBootstrapParameters) =
        postServer<ContinueSynchronizedBootstrapParameters, SynchronizedBootstrapPageDto> (parameters |> ensureCorrelationIdIsSet, "sync/bootstrap/continue")

    /// Reads repository-ordered accepted mutations after one protected cursor.
    static member public GetDeltas(parameters: GetSynchronizedDeltasParameters) =
        postServer<GetSynchronizedDeltasParameters, SynchronizedDeltaResultDto> (parameters |> ensureCorrelationIdIsSet, "sync/deltas/get")

    /// Submits one exact idempotent synchronized namespace or content mutation.
    static member public SubmitMutation(parameters: SubmitSynchronizedMutationParameters) =
        postServer<SubmitSynchronizedMutationParameters, SynchronizedOperationReceiptDto> (parameters |> ensureCorrelationIdIsSet, "sync/mutations/submit")

    /// Reads the deterministic receipt for one authorized operation identity.
    static member public GetOperation(parameters: GetSynchronizedOperationParameters) =
        postServer<GetSynchronizedOperationParameters, SynchronizedOperationReceiptDto> (parameters |> ensureCorrelationIdIsSet, "sync/operations/get")

    /// Prepares exact immutable bytes for a later synchronized mutation.
    static member public PrepareContent(parameters: PrepareSynchronizedContentParameters) =
        postServer<PrepareSynchronizedContentParameters, SynchronizedPreparedContentDto> (parameters |> ensureCorrelationIdIsSet, "sync/content/prepare")

    /// Creates one principal-bound, one-use read grant for retained synchronized bytes.
    static member public PrepareContentRead(parameters: PrepareSynchronizedContentReadParameters) =
        postServer<PrepareSynchronizedContentReadParameters, SynchronizedContentReadGrantDto> (parameters |> ensureCorrelationIdIsSet, "sync/content/read")

    /// Reads one current synchronized item after repository authorization.
    static member public GetItem(parameters: GetSynchronizedItemParameters) =
        postServer<GetSynchronizedItemParameters, SynchronizedItemDto> (parameters |> ensureCorrelationIdIsSet, "sync/items/get")

    /// Reads one normalized namespace slot and its current vacancy token.
    static member public GetNamespaceSlot(parameters: GetSynchronizedNamespaceSlotParameters) =
        postServer<GetSynchronizedNamespaceSlotParameters, SynchronizedNamespaceSlotDto> (parameters |> ensureCorrelationIdIsSet, "sync/namespace/get-slot")

    /// Reads content-free synchronization status for one repository.
    static member public GetStatus(parameters: GetSynchronizedStatusParameters) =
        postServer<GetSynchronizedStatusParameters, SynchronizedRepositoryStatusDto> (parameters |> ensureCorrelationIdIsSet, "sync/status/get")

    /// Redeems one authorized read grant and returns its exact immutable bytes.
    static member public DownloadContent(grantId: string, correlationId: string) : Task<GraceResult<byte array>> =
        task {
            let correlationId = ensureNonEmptyCorrelationId correlationId
            let route = $"sync/content/{Uri.EscapeDataString grantId}"

            try
                use httpClient = ClientIdentity.getHttpClient correlationId
                do! Auth.addAuthorizationHeader httpClient
                let! response = httpClient.GetAsync(Uri($"{resolveGraceServerUri ()}/{route}"))

                if response.IsSuccessStatusCode then
                    let! bytes = response.Content.ReadAsByteArrayAsync()

                    return
                        GraceReturnValue.Create bytes correlationId
                        |> Ok
                        |> ClientIdentity.enhanceWithLifecycleDiagnostics response
                else
                    let! error = ResponseErrors.fromResponse correlationId route response

                    return
                        Error error
                        |> ClientIdentity.enhanceWithLifecycleDiagnostics response
            with
            | ex ->
                let exceptionResponse = Utilities.ExceptionResponse.Create ex
                return Error(GraceError.Create ($"{exceptionResponse}") correlationId)
        }
