namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared
open Grace.Shared.Parameters.Library
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Library
open System
open System.Net.Http
open System.Threading.Tasks

/// SDK entry point for the complete authorized remote Libraries contract.
type Libraries() =

    /// Reads the current persisted Library configuration.
    static member public GetCatalog(parameters: GetLibraryCatalogParameters) =
        postServer<GetLibraryCatalogParameters, LibraryCatalogDto> (parameters |> ensureCorrelationIdIsSet, "libraries/catalog/get")

    /// Lists normalized Libraries in deterministic path order.
    static member public ListLibraries(parameters: ListLibrariesParameters) =
        postServer<ListLibrariesParameters, LibraryCatalogDto> (parameters |> ensureCorrelationIdIsSet, "libraries/list")

    /// Adds one Library under an exact current configuration version.
    static member public AddLibrary(parameters: AddLibraryParameters) =
        postServer<AddLibraryParameters, LibraryCatalogChangeResultDto> (parameters |> ensureCorrelationIdIsSet, "libraries/add")

    /// Removes one empty Library under an exact current configuration version.
    static member public RemoveLibrary(parameters: RemoveLibraryParameters) =
        postServer<RemoveLibraryParameters, LibraryCatalogChangeResultDto> (parameters |> ensureCorrelationIdIsSet, "libraries/remove")

    /// Starts one immutable baseline sequence from the latest published Library state.
    static member public StartBootstrap(parameters: StartLibraryBootstrapParameters) =
        postServer<StartLibraryBootstrapParameters, LibraryBootstrapPageDto> (parameters |> ensureCorrelationIdIsSet, "libraries/bootstrap/start")

    /// Continues one immutable baseline sequence using its protected page token.
    static member public ContinueBootstrap(parameters: ContinueLibraryBootstrapParameters) =
        postServer<ContinueLibraryBootstrapParameters, LibraryBootstrapPageDto> (parameters |> ensureCorrelationIdIsSet, "libraries/bootstrap/continue")

    /// Reads repository-ordered accepted changes after one protected cursor.
    static member public GetChanges(parameters: GetLibraryChangesParameters) =
        postServer<GetLibraryChangesParameters, LibraryChangePageDto> (parameters |> ensureCorrelationIdIsSet, "libraries/changes/get")

    /// Submits one exact idempotent Library namespace or content change.
    static member public SubmitChange(parameters: SubmitLibraryChangeParameters) =
        postServer<SubmitLibraryChangeParameters, LibraryOperationReceiptDto> (parameters |> ensureCorrelationIdIsSet, "libraries/changes/submit")

    /// Reads the deterministic receipt for one authorized operation identity.
    static member public GetOperation(parameters: GetLibraryOperationParameters) =
        postServer<GetLibraryOperationParameters, LibraryOperationReceiptDto> (parameters |> ensureCorrelationIdIsSet, "libraries/operations/get")

    /// Prepares exact immutable bytes for a later Library change.
    static member public PrepareContent(parameters: PrepareLibraryContentParameters) =
        postServer<PrepareLibraryContentParameters, LibraryPreparedContentDto> (parameters |> ensureCorrelationIdIsSet, "libraries/content/prepare")

    /// Creates one principal-bound, one-use read grant for retained Library bytes.
    static member public PrepareContentRead(parameters: PrepareLibraryContentReadParameters) =
        postServer<PrepareLibraryContentReadParameters, LibraryContentReadGrantDto> (parameters |> ensureCorrelationIdIsSet, "libraries/content/read")

    /// Reads one current Library item after repository authorization.
    static member public GetItem(parameters: GetLibraryItemParameters) =
        postServer<GetLibraryItemParameters, LibraryItemDto> (parameters |> ensureCorrelationIdIsSet, "libraries/items/get")

    /// Reads one normalized namespace slot and its current vacancy token.
    static member public GetNamespaceSlot(parameters: GetLibraryNamespaceSlotParameters) =
        postServer<GetLibraryNamespaceSlotParameters, LibraryNamespaceSlotDto> (parameters |> ensureCorrelationIdIsSet, "libraries/namespace/get-slot")

    /// Reads content-free synchronization status for one repository.
    static member public GetStatus(parameters: GetLibraryStatusParameters) =
        postServer<GetLibraryStatusParameters, LibraryRepositoryStatusDto> (parameters |> ensureCorrelationIdIsSet, "libraries/status/get")

    /// Redeems one authorized read grant and returns its exact immutable bytes.
    static member public DownloadContent(grantId: string, correlationId: string) : Task<GraceResult<byte array>> =
        task {
            let correlationId = ensureNonEmptyCorrelationId correlationId
            let route = $"libraries/content/{Uri.EscapeDataString grantId}"

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
