namespace Grace.Types

open Grace.Shared
open Grace.Types.Common
open Grace.Types.ArtifactGrant
open Grace.Types.Reference
open Orleans
open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions

/// Contains Materialization Plan request, response, artifact, and cache-selection contracts.
module MaterializationPlan =

    /// Preserves the established public Materialization Plan namespace for the shared execution enum.
    type MaterializationExecutionMode = Grace.Types.MaterializationExecutionMode

    /// Identifies the public target selector shape supplied before Grace resolves the immutable target root.
    type MaterializationTargetSelectorKind =
        | DirectoryVersionId = 1
        | ReferenceId = 2
        | BranchName = 3
        | ReferenceType = 4

    /// Identifies the cache behavior requested for artifact source selection.
    type MaterializationCacheSelectionKind =
        | BypassCache = 1
        | PreferCache = 2
        | RequireCache = 3

    /// Identifies the artifact contract vocabulary that a Materialization Plan may require.
    type MaterializationArtifactKind =
        | DirectoryVersionZip = 1
        | RecursiveDirectoryMetadata = 2
        | WholeFileContent = 3
        | FileManifest = 4
        | ContentBlock = 5

    /// Identifies where a planned artifact can be fetched or resolved from.
    type MaterializationArtifactSourceKind =
        | DirectUri = 1
        | CacheEntry = 2
        | Deferred = 3

    /// Builds the canonical artifact identity for the zip projection of a represented directory root.
    let canonicalDirectoryVersionZipIdentity (representedRootDirectoryVersionId: DirectoryVersionId) =
        $"{Constants.GraceZipFilesFolderName}/{representedRootDirectoryVersionId}.zip"

    /// Builds the canonical artifact identity for the recursive metadata projection of a represented directory root.
    let canonicalRecursiveDirectoryMetadataIdentity (representedRootDirectoryVersionId: DirectoryVersionId) = $"{representedRootDirectoryVersionId}.msgpack"

    /// Builds the canonical descriptor identity for whole-file bytes under one represented root.
    let canonicalWholeFileContentIdentity (representedRootDirectoryVersionId: DirectoryVersionId) (relativePath: RelativePath) =
        $"whole-file/{representedRootDirectoryVersionId}/{relativePath}"

    /// Builds the canonical descriptor identity for a manifest-backed artifact under one represented root.
    let canonicalFileManifestIdentity
        (representedRootDirectoryVersionId: DirectoryVersionId)
        (storagePoolId: StoragePoolId)
        (manifestAddress: ManifestAddress)
        =
        $"file-manifest/{representedRootDirectoryVersionId}/{storagePoolId}/{manifestAddress}"

    /// Builds the canonical descriptor identity for a content block artifact under one represented root.
    let canonicalContentBlockIdentity
        (representedRootDirectoryVersionId: DirectoryVersionId)
        (storagePoolId: StoragePoolId)
        (contentBlockAddress: ContentBlockAddress)
        =
        $"content-block/{representedRootDirectoryVersionId}/{storagePoolId}/{contentBlockAddress}"

    /// Selects a target before server-side resolution to one immutable root DirectoryVersionId.
    [<CLIMutable; GenerateSerializer>]
    type MaterializationTargetSelector =
        {
            Class: string
            SelectorKind: MaterializationTargetSelectorKind
            DirectoryVersionId: DirectoryVersionId option
            ReferenceId: ReferenceId option
            BranchId: BranchId option
            BranchName: BranchName option
            ReferenceType: ReferenceType option
        }

        /// Selects a known immutable directory version root directly.
        static member ForDirectoryVersion(directoryVersionId: DirectoryVersionId) =
            {
                Class = nameof MaterializationTargetSelector
                SelectorKind = MaterializationTargetSelectorKind.DirectoryVersionId
                DirectoryVersionId = Some directoryVersionId
                ReferenceId = None
                BranchId = None
                BranchName = None
                ReferenceType = None
            }

        /// Selects the directory version reached by a stored Grace reference.
        static member ForReference(referenceId: ReferenceId) =
            {
                Class = nameof MaterializationTargetSelector
                SelectorKind = MaterializationTargetSelectorKind.ReferenceId
                DirectoryVersionId = None
                ReferenceId = Some referenceId
                BranchId = None
                BranchName = None
                ReferenceType = None
            }

        /// Selects the directory version reached by the current branch tip at resolution time.
        static member ForBranch(branchName: BranchName) =
            {
                Class = nameof MaterializationTargetSelector
                SelectorKind = MaterializationTargetSelectorKind.BranchName
                DirectoryVersionId = None
                ReferenceId = None
                BranchId = None
                BranchName = Some branchName
                ReferenceType = None
            }

        /// Selects the latest reference of a given type for one branch name at server resolution time.
        static member ForReferenceType(branchName: BranchName, referenceType: ReferenceType) =
            {
                Class = nameof MaterializationTargetSelector
                SelectorKind = MaterializationTargetSelectorKind.ReferenceType
                DirectoryVersionId = None
                ReferenceId = None
                BranchId = None
                BranchName = Some branchName
                ReferenceType = Some referenceType
            }

        /// Selects the latest reference of a given type for one branch id at server resolution time.
        static member ForReferenceType(branchId: BranchId, referenceType: ReferenceType) =
            {
                Class = nameof MaterializationTargetSelector
                SelectorKind = MaterializationTargetSelectorKind.ReferenceType
                DirectoryVersionId = None
                ReferenceId = None
                BranchId = Some branchId
                BranchName = None
                ReferenceType = Some referenceType
            }

        /// Represents an empty selector used before caller input contributes target identity.
        static member Empty =
            {
                Class = nameof MaterializationTargetSelector
                SelectorKind = MaterializationTargetSelectorKind.DirectoryVersionId
                DirectoryVersionId = None
                ReferenceId = None
                BranchId = None
                BranchName = None
                ReferenceType = None
            }

    /// Records cache-selection intent separately from artifact source details.
    [<CLIMutable; GenerateSerializer>]
    type MaterializationCacheSelection =
        {
            Class: string
            SelectionKind: MaterializationCacheSelectionKind
        }

        /// Requests direct materialization without consulting cache entries.
        static member Bypass = { Class = nameof MaterializationCacheSelection; SelectionKind = MaterializationCacheSelectionKind.BypassCache }

        /// Allows Grace to prefer cache entries while still permitting direct artifact sources.
        static member Preferred = { Class = nameof MaterializationCacheSelection; SelectionKind = MaterializationCacheSelectionKind.PreferCache }

        /// Requires Grace to satisfy planned artifacts from cache sources only.
        static member Required = { Class = nameof MaterializationCacheSelection; SelectionKind = MaterializationCacheSelectionKind.RequireCache }

    /// Describes a fetchable or deferred location for one planned artifact.
    [<CLIMutable; GenerateSerializer>]
    type MaterializationArtifactSource =
        {
            Class: string
            SourceKind: MaterializationArtifactSourceKind
            DirectUri: string option
            CacheKey: string option
            CacheEndpoint: string option
            CacheId: string option
            DirectFallbackUri: string option
        }

        /// Points to a direct source URI produced by a materialization-capable server path.
        static member Direct(uri: string) =
            {
                Class = nameof MaterializationArtifactSource
                SourceKind = MaterializationArtifactSourceKind.DirectUri
                DirectUri = Some uri
                CacheKey = None
                CacheEndpoint = None
                CacheId = None
                DirectFallbackUri = None
            }

        /// Points to a cache entry without requiring Grace to expose a direct source URI.
        static member CacheOnly(cacheKey: string) =
            {
                Class = nameof MaterializationArtifactSource
                SourceKind = MaterializationArtifactSourceKind.CacheEntry
                DirectUri = None
                CacheKey = Some cacheKey
                CacheEndpoint = None
                CacheId = None
                DirectFallbackUri = None
            }

        /// Points to a selected Cache endpoint and optionally retains the explicit Preferred-mode Direct fallback.
        static member Cache(cacheKey: string, endpoint: string, cacheId: string, directFallbackUri: string option) =
            {
                Class = nameof MaterializationArtifactSource
                SourceKind = MaterializationArtifactSourceKind.CacheEntry
                DirectUri = None
                CacheKey = Some cacheKey
                CacheEndpoint = Some endpoint
                CacheId = Some cacheId
                DirectFallbackUri = directFallbackUri
            }

        /// Records that a later slice will resolve the artifact source during execution.
        static member Deferred =
            {
                Class = nameof MaterializationArtifactSource
                SourceKind = MaterializationArtifactSourceKind.Deferred
                DirectUri = None
                CacheKey = None
                CacheEndpoint = None
                CacheId = None
                DirectFallbackUri = None
            }

    /// Describes one stable artifact identity required to materialize a resolved target root.
    [<CLIMutable; GenerateSerializer>]
    type MaterializationArtifactDescriptor =
        {
            Class: string
            ArtifactKind: MaterializationArtifactKind
            CanonicalArtifactIdentity: string option
            RepresentedRootDirectoryVersionId: DirectoryVersionId option
            TargetRootDirectoryVersionId: DirectoryVersionId
            SizeInBytes: int64 option
            RelativePath: RelativePath option
            Sha256Hash: Sha256Hash option
            Blake3Hash: Blake3Hash option
            ManifestAddress: ManifestAddress option
            ContentBlockAddress: ContentBlockAddress option
            StoragePoolId: StoragePoolId option
            Source: MaterializationArtifactSource option
        }

        /// Describes the target-root zip artifact for a resolved immutable directory root.
        static member DirectoryVersionZip
            (
                targetRootDirectoryVersionId: DirectoryVersionId,
                sizeInBytes: int64,
                sha256Hash: Sha256Hash option,
                blake3Hash: Blake3Hash option,
                source: MaterializationArtifactSource option
            ) =
            {
                Class = nameof MaterializationArtifactDescriptor
                ArtifactKind = MaterializationArtifactKind.DirectoryVersionZip
                CanonicalArtifactIdentity = Some(canonicalDirectoryVersionZipIdentity targetRootDirectoryVersionId)
                RepresentedRootDirectoryVersionId = Some targetRootDirectoryVersionId
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                SizeInBytes = Some sizeInBytes
                RelativePath = None
                Sha256Hash = sha256Hash
                Blake3Hash = blake3Hash
                ManifestAddress = None
                ContentBlockAddress = None
                StoragePoolId = None
                Source = source
            }

        /// Describes recursive metadata for the same immutable target root as the plan response.
        static member RecursiveDirectoryMetadata
            (
                targetRootDirectoryVersionId: DirectoryVersionId,
                sizeInBytes: int64,
                sha256Hash: Sha256Hash option,
                blake3Hash: Blake3Hash option,
                source: MaterializationArtifactSource option
            ) =
            {
                Class = nameof MaterializationArtifactDescriptor
                ArtifactKind = MaterializationArtifactKind.RecursiveDirectoryMetadata
                CanonicalArtifactIdentity = Some(canonicalRecursiveDirectoryMetadataIdentity targetRootDirectoryVersionId)
                RepresentedRootDirectoryVersionId = Some targetRootDirectoryVersionId
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                SizeInBytes = Some sizeInBytes
                RelativePath = None
                Sha256Hash = sha256Hash
                Blake3Hash = blake3Hash
                ManifestAddress = None
                ContentBlockAddress = None
                StoragePoolId = None
                Source = source
            }

        /// Describes whole-file bytes by path plus stable content hash evidence.
        static member WholeFileContent
            (
                targetRootDirectoryVersionId: DirectoryVersionId,
                relativePath: RelativePath,
                sha256Hash: Sha256Hash option,
                blake3Hash: Blake3Hash option,
                source: MaterializationArtifactSource option
            ) =
            {
                Class = nameof MaterializationArtifactDescriptor
                ArtifactKind = MaterializationArtifactKind.WholeFileContent
                CanonicalArtifactIdentity = Some(canonicalWholeFileContentIdentity targetRootDirectoryVersionId relativePath)
                RepresentedRootDirectoryVersionId = Some targetRootDirectoryVersionId
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                SizeInBytes = None
                RelativePath = Some relativePath
                Sha256Hash = sha256Hash
                Blake3Hash = blake3Hash
                ManifestAddress = None
                ContentBlockAddress = None
                StoragePoolId = None
                Source = source
            }

        /// Describes a manifest-backed file by its CAS manifest identity and storage pool.
        static member FileManifest
            (
                targetRootDirectoryVersionId: DirectoryVersionId,
                manifestAddress: ManifestAddress,
                storagePoolId: StoragePoolId,
                source: MaterializationArtifactSource option
            ) =
            {
                Class = nameof MaterializationArtifactDescriptor
                ArtifactKind = MaterializationArtifactKind.FileManifest
                CanonicalArtifactIdentity = Some(canonicalFileManifestIdentity targetRootDirectoryVersionId storagePoolId manifestAddress)
                RepresentedRootDirectoryVersionId = Some targetRootDirectoryVersionId
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                SizeInBytes = None
                RelativePath = None
                Sha256Hash = None
                Blake3Hash = None
                ManifestAddress = Some manifestAddress
                ContentBlockAddress = None
                StoragePoolId = Some storagePoolId
                Source = source
            }

        /// Describes one reusable content block by its CAS block identity and storage pool.
        static member ContentBlock
            (
                targetRootDirectoryVersionId: DirectoryVersionId,
                contentBlockAddress: ContentBlockAddress,
                storagePoolId: StoragePoolId,
                source: MaterializationArtifactSource option
            ) =
            {
                Class = nameof MaterializationArtifactDescriptor
                ArtifactKind = MaterializationArtifactKind.ContentBlock
                CanonicalArtifactIdentity = Some(canonicalContentBlockIdentity targetRootDirectoryVersionId storagePoolId contentBlockAddress)
                RepresentedRootDirectoryVersionId = Some targetRootDirectoryVersionId
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                SizeInBytes = None
                RelativePath = None
                Sha256Hash = None
                Blake3Hash = None
                ManifestAddress = None
                ContentBlockAddress = Some contentBlockAddress
                StoragePoolId = Some storagePoolId
                Source = source
            }

    /// Requests a Materialization Plan for a selected target and execution/cache behavior.
    [<CLIMutable; GenerateSerializer>]
    type MaterializationPlanRequest =
        {
            Class: string
            TargetSelector: MaterializationTargetSelector
            ExecutionMode: MaterializationExecutionMode
            CacheSelection: MaterializationCacheSelection
            RequestedArtifactKinds: List<MaterializationArtifactKind>
            HolderPublicKey: ArtifactGrantHolderPublicKey option
        }

        /// Builds a request with a defensive copy of the requested artifact kind list.
        static member Create
            (
                targetSelector: MaterializationTargetSelector,
                executionMode: MaterializationExecutionMode,
                cacheSelection: MaterializationCacheSelection,
                requestedArtifactKinds: MaterializationArtifactKind seq
            ) =
            {
                Class = nameof MaterializationPlanRequest
                TargetSelector = targetSelector
                ExecutionMode = executionMode
                CacheSelection = cacheSelection
                RequestedArtifactKinds = List<MaterializationArtifactKind>(requestedArtifactKinds)
                HolderPublicKey = None
            }

        /// Adds the ephemeral holder public key required by cache-capable plan issuance.
        member this.WithHolderPublicKey(holderPublicKey: ArtifactGrantHolderPublicKey) = { this with HolderPublicKey = Some holderPublicKey }

    /// Responds with the resolved immutable target root and the artifacts required to materialize it.
    [<CLIMutable; GenerateSerializer>]
    type MaterializationPlan =
        {
            Class: string
            TargetRootDirectoryVersionId: DirectoryVersionId
            ExecutionMode: MaterializationExecutionMode
            CacheSelection: MaterializationCacheSelection
            RequiredArtifacts: List<MaterializationArtifactDescriptor>
            ArtifactGrant: SignedArtifactGrant option
        }

        /// Builds a response with a defensive copy of the required artifact descriptor list.
        static member Create
            (
                targetRootDirectoryVersionId: DirectoryVersionId,
                executionMode: MaterializationExecutionMode,
                cacheSelection: MaterializationCacheSelection,
                requiredArtifacts: MaterializationArtifactDescriptor seq
            ) =
            {
                Class = nameof MaterializationPlan
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                ExecutionMode = executionMode
                CacheSelection = cacheSelection
                RequiredArtifacts = List<MaterializationArtifactDescriptor>(requiredArtifacts)
                ArtifactGrant = None
            }

        /// Attaches the selected Cache grant only after the complete cache plan has been assembled.
        member this.WithArtifactGrant(grant: SignedArtifactGrant) = { this with ArtifactGrant = Some grant }

    /// Contains validation helpers for Materialization Plan contract invariants.
    module Validation =

        /// Returns true when the supplied enum value is one of the known public materialization execution modes.
        let isSupportedExecutionMode (mode: MaterializationExecutionMode) = Enum.IsDefined(typeof<MaterializationExecutionMode>, mode)

        /// Returns true when the supplied enum value is one of the known public materialization artifact kinds.
        let isSupportedArtifactKind (kind: MaterializationArtifactKind) = Enum.IsDefined(typeof<MaterializationArtifactKind>, kind)

        /// Returns true when the supplied enum value is one of the known public target selector kinds.
        let isSupportedTargetSelectorKind (kind: MaterializationTargetSelectorKind) = Enum.IsDefined(typeof<MaterializationTargetSelectorKind>, kind)

        /// Returns true when the supplied enum value is one of the known public cache selection kinds.
        let isSupportedCacheSelectionKind (kind: MaterializationCacheSelectionKind) = Enum.IsDefined(typeof<MaterializationCacheSelectionKind>, kind)

        /// Returns true when the supplied enum value is one of the known public artifact source kinds.
        let isSupportedArtifactSourceKind (kind: MaterializationArtifactSourceKind) = Enum.IsDefined(typeof<MaterializationArtifactSourceKind>, kind)

        let private canonicalLowercaseHexAddressRegex =
            Regex(
                "^[0-9a-f]{64}$",
                RegexOptions.Compiled
                ||| RegexOptions.CultureInvariant
            )

        /// Returns true when a contract hash or CAS address is a canonical lowercase 64-hex BLAKE3/SHA value.
        let isCanonicalLowercaseHexAddress (address: string) =
            not (String.IsNullOrWhiteSpace address)
            && canonicalLowercaseHexAddressRegex.IsMatch(address)

        /// Returns true when a direct artifact source can be fetched through a public HTTP(S) URI.
        let isAllowedDirectUri (uri: string) =
            let mutable parsedUri = Unchecked.defaultof<Uri>

            not (String.IsNullOrWhiteSpace uri)
            && Uri.TryCreate(uri, UriKind.Absolute, &parsedUri)
            && (parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || parsedUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))

        /// Returns true when a whole-file artifact path is normalized for repository-relative materialization.
        let isNormalizedRepositoryRelativePath (relativePath: string) =
            not (String.IsNullOrWhiteSpace relativePath)
            && not (Path.IsPathRooted relativePath)
            && not (relativePath.StartsWith("/", StringComparison.Ordinal))
            && not (relativePath.StartsWith("\\", StringComparison.Ordinal))
            && not (relativePath.Contains('\\'))
            && not (relativePath.Contains(':'))
            && relativePath.Split('/', StringSplitOptions.None)
               |> Array.forall (fun segment ->
                   not (String.IsNullOrWhiteSpace segment)
                   && segment <> "."
                   && segment <> "..")

        /// Rejects public execution/cache-selection combinations that have contradictory cache authority.
        let rejectUnsupportedExecutionCacheSelectionPair
            (executionMode: MaterializationExecutionMode)
            (cacheSelection: MaterializationCacheSelection)
            (errors: ResizeArray<string>)
            =
            if not (isNull (box cacheSelection)) then
                match executionMode, cacheSelection.SelectionKind with
                | MaterializationExecutionMode.Direct, MaterializationCacheSelectionKind.BypassCache -> ()
                | MaterializationExecutionMode.CachePreferred, MaterializationCacheSelectionKind.PreferCache -> ()
                | MaterializationExecutionMode.CacheRequired, MaterializationCacheSelectionKind.RequireCache -> ()
                | MaterializationExecutionMode.Direct, MaterializationCacheSelectionKind.RequireCache ->
                    errors.Add("Direct materialization must not use RequireCache selection.")
                | MaterializationExecutionMode.Direct, MaterializationCacheSelectionKind.PreferCache ->
                    errors.Add("Direct materialization must use BypassCache selection.")
                | MaterializationExecutionMode.CacheRequired, MaterializationCacheSelectionKind.BypassCache ->
                    errors.Add("CacheRequired materialization must not use BypassCache selection.")
                | MaterializationExecutionMode.CacheRequired, _ -> errors.Add("CacheRequired materialization must use RequireCache selection.")
                | MaterializationExecutionMode.CachePreferred, _ -> errors.Add("CachePreferred materialization must use PreferCache selection.")
                | _ -> ()

        /// Validates the target selector before server-side target-root resolution.
        let validateTargetSelector (selector: MaterializationTargetSelector) =
            let errors = ResizeArray<string>()

            if isNull (box selector) then
                errors.Add("TargetSelector is required.")
            else
                if selector.Class
                   <> nameof MaterializationTargetSelector then
                    errors.Add("TargetSelector.Class must be MaterializationTargetSelector.")

                if not (isSupportedTargetSelectorKind selector.SelectorKind) then
                    errors.Add($"TargetSelector.SelectorKind '{int selector.SelectorKind}' is not supported.")
                else
                    match selector.SelectorKind with
                    | MaterializationTargetSelectorKind.DirectoryVersionId ->
                        match selector.DirectoryVersionId with
                        | Some directoryVersionId when directoryVersionId <> DirectoryVersionId.Empty -> ()
                        | _ -> errors.Add("TargetSelector.DirectoryVersionId is required.")

                        if selector.ReferenceId.IsSome then
                            errors.Add("TargetSelector.ReferenceId must be empty for DirectoryVersionId selectors.")

                        if selector.BranchId.IsSome then
                            errors.Add("TargetSelector.BranchId must be empty for DirectoryVersionId selectors.")

                        if selector.BranchName.IsSome then
                            errors.Add("TargetSelector.BranchName must be empty for DirectoryVersionId selectors.")

                        if selector.ReferenceType.IsSome then
                            errors.Add("TargetSelector.ReferenceType must be empty for DirectoryVersionId selectors.")
                    | MaterializationTargetSelectorKind.ReferenceId ->
                        match selector.ReferenceId with
                        | Some referenceId when referenceId <> ReferenceId.Empty -> ()
                        | _ -> errors.Add("TargetSelector.ReferenceId is required.")

                        if selector.DirectoryVersionId.IsSome then
                            errors.Add("TargetSelector.DirectoryVersionId must be empty for ReferenceId selectors.")

                        if selector.BranchId.IsSome then
                            errors.Add("TargetSelector.BranchId must be empty for ReferenceId selectors.")

                        if selector.BranchName.IsSome then
                            errors.Add("TargetSelector.BranchName must be empty for ReferenceId selectors.")

                        if selector.ReferenceType.IsSome then
                            errors.Add("TargetSelector.ReferenceType must be empty for ReferenceId selectors.")
                    | MaterializationTargetSelectorKind.BranchName ->
                        match selector.BranchName with
                        | Some branchName when
                            not (String.IsNullOrWhiteSpace branchName)
                            && Constants.GraceNameRegex.IsMatch(branchName)
                            ->
                            ()
                        | Some branchName when not (String.IsNullOrWhiteSpace branchName) ->
                            errors.Add("TargetSelector.BranchName must be a valid Grace branch name.")
                        | _ -> errors.Add("TargetSelector.BranchName is required.")

                        if selector.DirectoryVersionId.IsSome then
                            errors.Add("TargetSelector.DirectoryVersionId must be empty for BranchName selectors.")

                        if selector.ReferenceId.IsSome then
                            errors.Add("TargetSelector.ReferenceId must be empty for BranchName selectors.")

                        if selector.BranchId.IsSome then
                            errors.Add("TargetSelector.BranchId must be empty for BranchName selectors.")

                        if selector.ReferenceType.IsSome then
                            errors.Add("TargetSelector.ReferenceType must be empty for BranchName selectors.")
                    | MaterializationTargetSelectorKind.ReferenceType ->
                        let hasBranchId =
                            match selector.BranchId with
                            | Some branchId when branchId <> BranchId.Empty -> true
                            | Some _ ->
                                errors.Add("TargetSelector.BranchId is required when supplied for ReferenceType selectors.")
                                false
                            | None -> false

                        let hasBranchName =
                            match selector.BranchName with
                            | Some branchName when
                                not (String.IsNullOrWhiteSpace branchName)
                                && Constants.GraceNameRegex.IsMatch(branchName)
                                ->
                                true
                            | Some branchName when not (String.IsNullOrWhiteSpace branchName) ->
                                errors.Add("TargetSelector.BranchName must be a valid Grace branch name.")
                                false
                            | Some _ -> false
                            | None -> false

                        if hasBranchId = hasBranchName then
                            errors.Add("TargetSelector must include exactly one of BranchId or BranchName for ReferenceType selectors.")

                        match selector.ReferenceType with
                        | Some _ -> ()
                        | _ -> errors.Add("TargetSelector.ReferenceType is required.")

                        if selector.DirectoryVersionId.IsSome then
                            errors.Add("TargetSelector.DirectoryVersionId must be empty for ReferenceType selectors.")

                        if selector.ReferenceId.IsSome then
                            errors.Add("TargetSelector.ReferenceId must be empty for ReferenceType selectors.")
                    | _ -> errors.Add($"TargetSelector.SelectorKind '{int selector.SelectorKind}' is not supported.")

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)

        /// Validates cache-selection intent without assuming any server runtime behavior.
        let validateCacheSelection (cacheSelection: MaterializationCacheSelection) =
            let errors = ResizeArray<string>()

            if isNull (box cacheSelection) then
                errors.Add("CacheSelection is required.")
            else
                if cacheSelection.Class
                   <> nameof MaterializationCacheSelection then
                    errors.Add("CacheSelection.Class must be MaterializationCacheSelection.")

                if not (isSupportedCacheSelectionKind cacheSelection.SelectionKind) then
                    errors.Add($"CacheSelection.SelectionKind '{int cacheSelection.SelectionKind}' is not supported.")

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)

        /// Validates that a source shape is explicit and does not invent direct URLs for cache-only artifacts.
        let validateArtifactSource (source: MaterializationArtifactSource) =
            let errors = ResizeArray<string>()

            if isNull (box source) then
                errors.Add("Artifact Source is required.")
            else
                if source.Class
                   <> nameof MaterializationArtifactSource then
                    errors.Add("Artifact Source Class must be MaterializationArtifactSource.")

                if not (isSupportedArtifactSourceKind source.SourceKind) then
                    errors.Add($"Artifact SourceKind '{int source.SourceKind}' is not supported.")
                else
                    match source.SourceKind with
                    | MaterializationArtifactSourceKind.DirectUri ->
                        match source.DirectUri with
                        | Some uri when isAllowedDirectUri uri -> ()
                        | Some _ -> errors.Add("Artifact DirectUri must be an absolute http or https URI for DirectUri sources.")
                        | _ -> errors.Add("Artifact DirectUri is required for DirectUri sources.")

                        if source.CacheKey.IsSome then
                            errors.Add("Artifact CacheKey must be empty for DirectUri sources.")

                        if source.CacheEndpoint.IsSome
                           || source.CacheId.IsSome
                           || source.DirectFallbackUri.IsSome then
                            errors.Add("DirectUri sources must not contain Cache endpoint, principal, or fallback fields.")
                    | MaterializationArtifactSourceKind.CacheEntry ->
                        match source.CacheKey with
                        | Some cacheKey when not (String.IsNullOrWhiteSpace cacheKey) -> ()
                        | _ -> errors.Add("Artifact CacheKey is required for CacheEntry sources.")

                        if source.DirectUri.IsSome then
                            errors.Add("Artifact DirectUri must be empty for CacheEntry sources.")

                        match source.CacheEndpoint, source.CacheId with
                        | None, None -> ()
                        | Some endpoint, Some principal when
                            isAllowedDirectUri endpoint
                            && not (String.IsNullOrWhiteSpace principal)
                            ->
                            ()
                        | _ -> errors.Add("CacheEntry source endpoint and service principal must be valid and specified together.")

                        match source.DirectFallbackUri with
                        | Some uri when not (isAllowedDirectUri uri) -> errors.Add("Artifact DirectFallbackUri must be an absolute http or https URI.")
                        | _ -> ()
                    | MaterializationArtifactSourceKind.Deferred ->
                        if source.DirectUri.IsSome then
                            errors.Add("Artifact DirectUri must be empty for Deferred sources.")

                        if source.CacheKey.IsSome then
                            errors.Add("Artifact CacheKey must be empty for Deferred sources.")

                        if source.CacheEndpoint.IsSome
                           || source.CacheId.IsSome
                           || source.DirectFallbackUri.IsSome then
                            errors.Add("Deferred sources must not contain Cache endpoint, principal, or fallback fields.")
                    | _ -> errors.Add($"Artifact SourceKind '{int source.SourceKind}' is not supported.")

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)

        /// Validates that an artifact descriptor carries the stable identity required by its kind.
        let validateArtifactDescriptor (descriptor: MaterializationArtifactDescriptor) =
            let errors = ResizeArray<string>()

            let requireTargetRoot () =
                if descriptor.TargetRootDirectoryVersionId = DirectoryVersionId.Empty then
                    errors.Add("Artifact TargetRootDirectoryVersionId is required.")

            let requireRepresentedRoot artifactKind =
                match descriptor.RepresentedRootDirectoryVersionId with
                | Some representedRootDirectoryVersionId when representedRootDirectoryVersionId = DirectoryVersionId.Empty ->
                    errors.Add($"Artifact RepresentedRootDirectoryVersionId is required for {artifactKind} descriptors.")
                | Some representedRootDirectoryVersionId when representedRootDirectoryVersionId = descriptor.TargetRootDirectoryVersionId -> ()
                | Some _ -> errors.Add("Artifact RepresentedRootDirectoryVersionId must match TargetRootDirectoryVersionId.")
                | None -> errors.Add($"Artifact RepresentedRootDirectoryVersionId is required for {artifactKind} descriptors.")

            let requireCanonicalArtifactIdentity expected artifactKind =
                match descriptor.CanonicalArtifactIdentity with
                | Some artifactIdentity when String.Equals(artifactIdentity, expected, StringComparison.Ordinal) -> ()
                | Some _ -> errors.Add($"Artifact CanonicalArtifactIdentity must be {expected} for {artifactKind} descriptors.")
                | None -> errors.Add($"Artifact CanonicalArtifactIdentity is required for {artifactKind} descriptors.")

            let validateOptionalSize artifactKind =
                match descriptor.SizeInBytes with
                | Some sizeInBytes when sizeInBytes >= 0L -> ()
                | Some _ -> errors.Add($"Artifact SizeInBytes must be non-negative for {artifactKind} descriptors.")
                | None -> ()

            let requireSize artifactKind =
                match descriptor.SizeInBytes with
                | Some sizeInBytes when sizeInBytes >= 0L -> ()
                | Some _ -> errors.Add($"Artifact SizeInBytes must be non-negative for {artifactKind} descriptors.")
                | None -> errors.Add($"Artifact SizeInBytes is required for {artifactKind} descriptors.")

            let requireStoragePool () =
                match descriptor.StoragePoolId with
                | Some storagePoolId when not (String.IsNullOrWhiteSpace storagePoolId) -> ()
                | _ -> errors.Add("Artifact StoragePoolId is required for CAS artifact descriptors.")

            let requireCanonicalSha256Hash artifactKind =
                match descriptor.Sha256Hash with
                | Some sha256Hash when isCanonicalLowercaseHexAddress sha256Hash -> ()
                | Some _ -> errors.Add($"Artifact Sha256Hash must be a canonical lowercase 64-character hexadecimal value for {artifactKind} descriptors.")
                | None -> ()

            let requireCanonicalBlake3Hash artifactKind =
                match descriptor.Blake3Hash with
                | Some blake3Hash when isCanonicalLowercaseHexAddress blake3Hash -> ()
                | Some _ -> errors.Add($"Artifact Blake3Hash must be a canonical lowercase 64-character hexadecimal value for {artifactKind} descriptors.")
                | None -> ()

            let requireAnyIntegrity artifactKind =
                requireCanonicalSha256Hash artifactKind
                requireCanonicalBlake3Hash artifactKind

                if descriptor.Sha256Hash.IsNone
                   && descriptor.Blake3Hash.IsNone then
                    errors.Add($"Artifact Sha256Hash or Blake3Hash is required for {artifactKind} descriptors.")

            let rejectRelativePath artifactKind =
                if descriptor.RelativePath.IsSome then
                    errors.Add($"Artifact RelativePath must be empty for {artifactKind} descriptors.")

            let rejectHashes artifactKind =
                if descriptor.Sha256Hash.IsSome then
                    errors.Add($"Artifact Sha256Hash must be empty for {artifactKind} descriptors.")

                if descriptor.Blake3Hash.IsSome then
                    errors.Add($"Artifact Blake3Hash must be empty for {artifactKind} descriptors.")

            let rejectManifestAddress artifactKind =
                if descriptor.ManifestAddress.IsSome then
                    errors.Add($"Artifact ManifestAddress must be empty for {artifactKind} descriptors.")

            let rejectContentBlockAddress artifactKind =
                if descriptor.ContentBlockAddress.IsSome then
                    errors.Add($"Artifact ContentBlockAddress must be empty for {artifactKind} descriptors.")

            let rejectStoragePool artifactKind =
                if descriptor.StoragePoolId.IsSome then
                    errors.Add($"Artifact StoragePoolId must be empty for {artifactKind} descriptors.")

            let rejectNonRootDescriptorIdentity artifactKind =
                rejectRelativePath artifactKind
                rejectManifestAddress artifactKind
                rejectContentBlockAddress artifactKind
                rejectStoragePool artifactKind

            if isNull (box descriptor) then
                errors.Add("Artifact descriptor is required.")
            else
                if descriptor.Class
                   <> nameof MaterializationArtifactDescriptor then
                    errors.Add("Artifact Class must be MaterializationArtifactDescriptor.")

                if not (isSupportedArtifactKind descriptor.ArtifactKind) then
                    errors.Add($"ArtifactKind '{int descriptor.ArtifactKind}' is not supported.")
                else
                    let artifactKind = string descriptor.ArtifactKind

                    requireTargetRoot ()
                    requireRepresentedRoot artifactKind

                    match descriptor.ArtifactKind with
                    | MaterializationArtifactKind.DirectoryVersionZip ->
                        let expectedIdentity = canonicalDirectoryVersionZipIdentity descriptor.TargetRootDirectoryVersionId

                        requireCanonicalArtifactIdentity expectedIdentity artifactKind
                        requireSize artifactKind
                        requireAnyIntegrity artifactKind
                        rejectNonRootDescriptorIdentity artifactKind
                    | MaterializationArtifactKind.RecursiveDirectoryMetadata ->
                        let expectedIdentity = canonicalRecursiveDirectoryMetadataIdentity descriptor.TargetRootDirectoryVersionId

                        requireCanonicalArtifactIdentity expectedIdentity artifactKind
                        requireSize artifactKind
                        requireAnyIntegrity artifactKind
                        rejectNonRootDescriptorIdentity artifactKind
                    | MaterializationArtifactKind.WholeFileContent ->
                        validateOptionalSize artifactKind

                        match descriptor.RelativePath with
                        | Some relativePath ->
                            requireCanonicalArtifactIdentity
                                (canonicalWholeFileContentIdentity descriptor.TargetRootDirectoryVersionId relativePath)
                                artifactKind
                        | None -> ()

                        match descriptor.RelativePath with
                        | Some relativePath when isNormalizedRepositoryRelativePath relativePath -> ()
                        | Some _ -> errors.Add("Artifact RelativePath must be a normalized repository-relative path for WholeFileContent descriptors.")
                        | _ -> errors.Add("Artifact RelativePath is required for WholeFileContent descriptors.")

                        requireAnyIntegrity artifactKind

                        rejectManifestAddress artifactKind
                        rejectContentBlockAddress artifactKind
                        rejectStoragePool artifactKind
                    | MaterializationArtifactKind.FileManifest ->
                        validateOptionalSize artifactKind

                        match descriptor.ManifestAddress with
                        | Some manifestAddress when isCanonicalLowercaseHexAddress manifestAddress ->
                            match descriptor.StoragePoolId with
                            | Some storagePoolId when not (String.IsNullOrWhiteSpace storagePoolId) ->
                                requireCanonicalArtifactIdentity
                                    (canonicalFileManifestIdentity descriptor.TargetRootDirectoryVersionId storagePoolId manifestAddress)
                                    artifactKind
                            | _ -> ()
                        | Some _ ->
                            errors.Add("Artifact ManifestAddress must be a canonical lowercase 64-character hexadecimal value for FileManifest descriptors.")
                        | _ -> errors.Add("Artifact ManifestAddress is required for FileManifest descriptors.")

                        requireStoragePool ()
                        rejectRelativePath artifactKind
                        rejectHashes artifactKind
                        rejectContentBlockAddress artifactKind
                    | MaterializationArtifactKind.ContentBlock ->
                        validateOptionalSize artifactKind

                        match descriptor.ContentBlockAddress with
                        | Some contentBlockAddress when isCanonicalLowercaseHexAddress contentBlockAddress ->
                            match descriptor.StoragePoolId with
                            | Some storagePoolId when not (String.IsNullOrWhiteSpace storagePoolId) ->
                                requireCanonicalArtifactIdentity
                                    (canonicalContentBlockIdentity descriptor.TargetRootDirectoryVersionId storagePoolId contentBlockAddress)
                                    artifactKind
                            | _ -> ()
                        | Some _ ->
                            errors.Add(
                                "Artifact ContentBlockAddress must be a canonical lowercase 64-character hexadecimal value for ContentBlock descriptors."
                            )
                        | _ -> errors.Add("Artifact ContentBlockAddress is required for ContentBlock descriptors.")

                        requireStoragePool ()
                        rejectRelativePath artifactKind
                        rejectHashes artifactKind
                        rejectManifestAddress artifactKind
                    | _ -> errors.Add($"ArtifactKind '{int descriptor.ArtifactKind}' is not supported.")

                match descriptor.Source with
                | Some source ->
                    match validateArtifactSource source with
                    | Ok () -> ()
                    | Error sourceErrors -> errors.AddRange(sourceErrors)
                | None -> ()

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)

        /// Validates that a Materialization Plan request only accepts supported modes, selectors, and artifact kinds.
        let validateRequest (request: MaterializationPlanRequest) =
            let errors = ResizeArray<string>()

            if isNull (box request) then
                errors.Add("MaterializationPlanRequest is required.")
            else
                if request.Class <> nameof MaterializationPlanRequest then
                    errors.Add("Class must be MaterializationPlanRequest.")

                if not (isSupportedExecutionMode request.ExecutionMode) then
                    errors.Add($"ExecutionMode '{int request.ExecutionMode}' is not supported.")

                match validateTargetSelector request.TargetSelector with
                | Ok () -> ()
                | Error selectorErrors -> errors.AddRange(selectorErrors)

                match validateCacheSelection request.CacheSelection with
                | Ok () -> ()
                | Error cacheErrors -> errors.AddRange(cacheErrors)

                rejectUnsupportedExecutionCacheSelectionPair request.ExecutionMode request.CacheSelection errors

                if
                    isNull (box request.RequestedArtifactKinds)
                    || request.RequestedArtifactKinds.Count = 0
                then
                    errors.Add("RequestedArtifactKinds must include at least one artifact kind.")
                else
                    for kind in request.RequestedArtifactKinds do
                        if not (isSupportedArtifactKind kind) then
                            errors.Add($"Requested ArtifactKind '{int kind}' is not supported.")

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)

        /// Validates that a Materialization Plan resolves one root and includes the V1 required root artifacts.
        let validatePlan (plan: MaterializationPlan) =
            let errors = ResizeArray<string>()

            if isNull (box plan) then
                errors.Add("MaterializationPlan is required.")
            else
                if plan.Class <> nameof MaterializationPlan then
                    errors.Add("Class must be MaterializationPlan.")

                if plan.TargetRootDirectoryVersionId = DirectoryVersionId.Empty then
                    errors.Add("TargetRootDirectoryVersionId is required.")

                if not (isSupportedExecutionMode plan.ExecutionMode) then
                    errors.Add($"ExecutionMode '{int plan.ExecutionMode}' is not supported.")

                match validateCacheSelection plan.CacheSelection with
                | Ok () -> ()
                | Error cacheErrors -> errors.AddRange(cacheErrors)

                rejectUnsupportedExecutionCacheSelectionPair plan.ExecutionMode plan.CacheSelection errors

                if
                    isNull (box plan.RequiredArtifacts)
                    || plan.RequiredArtifacts.Count = 0
                then
                    errors.Add("RequiredArtifacts must include the V1 target-root artifacts.")
                else
                    let mutable targetRootZipCount = 0
                    let mutable recursiveMetadataCount = 0
                    let wholeFileIdentities = HashSet<DirectoryVersionId * RelativePath>()
                    let fileManifestIdentities = HashSet<DirectoryVersionId * StoragePoolId * ManifestAddress>()
                    let contentBlockIdentities = HashSet<DirectoryVersionId * StoragePoolId * ContentBlockAddress>()
                    let cacheRequiredByExecutionMode = plan.ExecutionMode = MaterializationExecutionMode.CacheRequired
                    let cacheBypassedByExecutionMode = plan.ExecutionMode = MaterializationExecutionMode.Direct

                    let cacheRequiredBySelection =
                        not (isNull (box plan.CacheSelection))
                        && plan.CacheSelection.SelectionKind = MaterializationCacheSelectionKind.RequireCache

                    let cacheBypassedBySelection =
                        not (isNull (box plan.CacheSelection))
                        && plan.CacheSelection.SelectionKind = MaterializationCacheSelectionKind.BypassCache

                    for descriptor in plan.RequiredArtifacts do
                        match validateArtifactDescriptor descriptor with
                        | Ok () -> ()
                        | Error descriptorErrors -> errors.AddRange(descriptorErrors)

                        if not (isNull (box descriptor)) then
                            if cacheBypassedByExecutionMode
                               || cacheBypassedBySelection then
                                match descriptor.Source with
                                | Some source when
                                    not (isNull (box source))
                                    && source.SourceKind = MaterializationArtifactSourceKind.CacheEntry
                                    ->
                                    errors.Add("Direct/Bypass plans must not require CacheEntry artifact sources.")
                                | _ -> ()

                            if cacheRequiredByExecutionMode
                               || cacheRequiredBySelection then
                                match descriptor.Source with
                                | Some source when
                                    not (isNull (box source))
                                    && source.SourceKind = MaterializationArtifactSourceKind.CacheEntry
                                    ->
                                    if source.DirectFallbackUri.IsSome then
                                        errors.Add("CacheRequired plans must not contain Direct fallback retrieval details.")
                                | Some source when
                                    not (isNull (box source))
                                    && source.SourceKind = MaterializationArtifactSourceKind.DirectUri
                                    ->
                                    errors.Add("CacheRequired plans must require CacheEntry artifact sources for every required artifact.")
                                | Some _ -> errors.Add("CacheRequired plans must require CacheEntry artifact sources for every required artifact.")
                                | None -> errors.Add("CacheRequired plans must require CacheEntry artifact sources for every required artifact.")

                            if descriptor.TargetRootDirectoryVersionId
                               <> plan.TargetRootDirectoryVersionId then
                                errors.Add("Artifact TargetRootDirectoryVersionId must match the plan TargetRootDirectoryVersionId.")

                            if descriptor.ArtifactKind = MaterializationArtifactKind.DirectoryVersionZip
                               && descriptor.TargetRootDirectoryVersionId = plan.TargetRootDirectoryVersionId then
                                targetRootZipCount <- targetRootZipCount + 1

                            if descriptor.ArtifactKind = MaterializationArtifactKind.RecursiveDirectoryMetadata
                               && descriptor.TargetRootDirectoryVersionId = plan.TargetRootDirectoryVersionId then
                                recursiveMetadataCount <- recursiveMetadataCount + 1

                            if descriptor.ArtifactKind = MaterializationArtifactKind.WholeFileContent then
                                match descriptor.RelativePath with
                                | Some relativePath when
                                    not (String.IsNullOrWhiteSpace relativePath)
                                    && not (wholeFileIdentities.Add(descriptor.TargetRootDirectoryVersionId, relativePath))
                                    ->
                                    errors.Add("RequiredArtifacts must include at most one WholeFileContent for each target root and relative path.")
                                | _ -> ()

                            if descriptor.ArtifactKind = MaterializationArtifactKind.FileManifest then
                                match descriptor.StoragePoolId, descriptor.ManifestAddress with
                                | Some storagePoolId, Some manifestAddress when
                                    not (String.IsNullOrWhiteSpace storagePoolId)
                                    && not (String.IsNullOrWhiteSpace manifestAddress)
                                    && not (fileManifestIdentities.Add(descriptor.TargetRootDirectoryVersionId, storagePoolId, manifestAddress))
                                    ->
                                    errors.Add(
                                        "RequiredArtifacts must include at most one FileManifest for each target root, storage pool, and manifest address."
                                    )
                                | _ -> ()

                            if descriptor.ArtifactKind = MaterializationArtifactKind.ContentBlock then
                                match descriptor.StoragePoolId, descriptor.ContentBlockAddress with
                                | Some storagePoolId, Some contentBlockAddress when
                                    not (String.IsNullOrWhiteSpace storagePoolId)
                                    && not (String.IsNullOrWhiteSpace contentBlockAddress)
                                    && not (contentBlockIdentities.Add(descriptor.TargetRootDirectoryVersionId, storagePoolId, contentBlockAddress))
                                    ->
                                    errors.Add(
                                        "RequiredArtifacts must include at most one ContentBlock for each target root, storage pool, and content block address."
                                    )
                                | _ -> ()

                    if targetRootZipCount = 0 then
                        errors.Add("RequiredArtifacts must include DirectoryVersionZip for the target root.")
                    elif targetRootZipCount > 1 then
                        errors.Add("RequiredArtifacts must include exactly one DirectoryVersionZip for the target root.")

                    if recursiveMetadataCount = 0 then
                        errors.Add("RequiredArtifacts must include RecursiveDirectoryMetadata for the target root.")
                    elif recursiveMetadataCount > 1 then
                        errors.Add("RequiredArtifacts must include exactly one RecursiveDirectoryMetadata for the target root.")

                    let cacheBackedArtifacts =
                        plan.RequiredArtifacts
                        |> Seq.exists (fun artifact ->
                            match artifact.Source with
                            | Some source when not (isNull (box source)) -> source.SourceKind = MaterializationArtifactSourceKind.CacheEntry
                            | _ -> false)

                    let allSourcesDirect =
                        plan.RequiredArtifacts
                        |> Seq.forall (fun artifact ->
                            match artifact.Source with
                            | Some source when not (isNull (box source)) -> source.SourceKind = MaterializationArtifactSourceKind.DirectUri
                            | _ -> false)

                    let allSourcesPresent =
                        plan.RequiredArtifacts
                        |> Seq.forall (fun artifact -> artifact.Source.IsSome)

                    match plan.ArtifactGrant with
                    | None when
                        errors.Count = 0
                        && allSourcesPresent
                        && (plan.ExecutionMode = MaterializationExecutionMode.CacheRequired
                            || cacheBackedArtifacts
                            || not allSourcesDirect)
                        ->
                        errors.Add("ArtifactGrant is required for CacheRequired plans, CacheEntry sources, and plans that are not entirely Direct.")
                    | None -> ()
                    | Some grant when isNull (box grant) || isNull (box grant.Payload) -> errors.Add("ArtifactGrant must contain a signed payload.")
                    | Some grant ->
                        let payload = grant.Payload

                        let plannedIdentities =
                            plan.RequiredArtifacts
                            |> Seq.choose (fun artifact -> artifact.CanonicalArtifactIdentity)
                            |> Set.ofSeq

                        let grantedIdentities =
                            if isNull (box payload.ArtifactIdentities) then
                                Set.empty
                            else
                                payload.ArtifactIdentities |> Set.ofSeq

                        if payload.TargetRootDirectoryVersionId
                           <> plan.TargetRootDirectoryVersionId then
                            errors.Add("ArtifactGrant target root must match the Materialization Plan target root.")

                        if payload.ExecutionMode <> plan.ExecutionMode then
                            errors.Add("ArtifactGrant execution mode must match the Materialization Plan execution mode.")

                        if grantedIdentities <> plannedIdentities then
                            errors.Add("ArtifactGrant artifact identities must exactly match the Materialization Plan artifacts.")

                        for artifact in plan.RequiredArtifacts do
                            match artifact.Source with
                            | Some source when source.SourceKind = MaterializationArtifactSourceKind.CacheEntry ->
                                match source.CacheId with
                                | Some principal when principal = payload.CacheId -> ()
                                | _ -> errors.Add("ArtifactGrant CacheId must match every Cache source.")
                            | _ -> ()

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)
