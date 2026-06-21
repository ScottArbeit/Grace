namespace Grace.Shared

open Grace.Shared.Parameters.Storage
open Grace.Types.ContentBlockMetadata
open Grace.Types.Repository
open Grace.Types.Common
open Grace.Types.UploadSession
open NodaTime
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text

module DedupeIndex =

    type FinalizedManifestIndexSource =
        {
            StoragePoolId: StoragePoolId
            Session: UploadSessionDto
            Manifest: FileManifest
            BlockPayloads: FinalizeManifestBlockPayload array
            Metadata: ContentBlockMetadata array
        }

    type DedupeIndexRecord =
        {
            StoragePoolId: StoragePoolId
            ManifestAddress: ManifestAddress
            ContentBlockAddress: ContentBlockAddress
            OrdinalStart: int
            OrdinalCount: int
            MetadataVersion: MetadataVersion
            ProtectedChunkAddresses: string array
        }

    type FinalizedManifestRegistration =
        {
            StoragePoolId: StoragePoolId
            Session: UploadSessionDto
            Manifest: FileManifest
            BlockPayloads: FinalizeManifestBlockPayload array
        }

    type RegisteredContentBlock = { Address: ContentBlockAddress; ChunkAddresses: ChunkAddress array }

    type RuntimeFinalizedManifestRegistration =
        {
            StoragePoolId: StoragePoolId
            Session: UploadSessionDto
            ManifestAddress: ManifestAddress
            Blocks: RegisteredContentBlock array
        }

    type DedupeIndexState =
        {
            Records: DedupeIndexRecord array
            FinalizedManifests: RuntimeFinalizedManifestRegistration array
            MetadataRecords: ContentBlockMetadata array
        }

        static member Empty = { Records = Array.empty; FinalizedManifests = Array.empty; MetadataRecords = Array.empty }

    let private globalGate = obj ()
    let mutable private globalState = DedupeIndexState.Empty

    let discoveryPolicy () : ContentBlockDiscoveryPolicy =
        {
            MaxKeyChunkAddresses = MaxDiscoveryKeyChunkAddresses
            MaxCandidateWindowsPerKeyChunk = MaxCandidateWindowsPerKeyChunk
            MaxWindowChunks = MaxWindowChunks
            MaxResponseProtectedChunks = MaxResponseProtectedChunks
            ResponseTtlSeconds = ResponseTtlSeconds
            MinimumAcceptedReuseRunLength = MinimumAcceptedReuseRunLength
            PositiveCandidatesEnabled = true
            EmptyResponseMeansAbsent = false
            IsAuthoritative = false
        }

    let storagePoolIdForRepositoryId (repositoryId: RepositoryId) = StoragePoolRouting.repositoryDedupeStoragePoolId repositoryId

    let storagePoolIdForRepository (repositoryDto: RepositoryDto) =
        if isNull (box repositoryDto) then
            invalidOp "Repository state is required before resolving a StoragePool route."
        elif String.IsNullOrWhiteSpace repositoryDto.StoragePoolId then
            invalidOp "Repository StoragePoolId is not configured."
        elif repositoryDto.StoragePoolId
             <> StoragePoolRouting.defaultStoragePoolId then
            invalidOp $"StoragePoolId '{repositoryDto.StoragePoolId}' is not configured. StoragePool routing fails closed."
        else
            storagePoolIdForRepositoryId repositoryDto.RepositoryId

    let private protectChunkAddress (storagePoolId: StoragePoolId) (chunkAddress: ChunkAddress) =
        let preimage = $"grace.dedupe-index.v1.protected-window\n{storagePoolId}\n{chunkAddress}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage))
        $"protected-sha256:{Convert.ToHexString(hash).ToLowerInvariant()}"

    let private recordKey (record: DedupeIndexRecord) =
        $"{record.StoragePoolId}|{record.ManifestAddress}|{record.ContentBlockAddress}|{record.OrdinalStart}|{record.OrdinalCount}|{record.MetadataVersion}"

    let private finalizedManifestKey (registration: FinalizedManifestRegistration) =
        $"{registration.StoragePoolId}|{registration.Session.UploadSessionId}|{registration.Manifest.ManifestAddress}"

    let private runtimeFinalizedManifestKey (registration: RuntimeFinalizedManifestRegistration) =
        $"{registration.StoragePoolId}|{registration.Session.UploadSessionId}|{registration.ManifestAddress}"

    let private metadataKey (metadata: ContentBlockMetadata) = $"{metadata.StoragePoolId}|{metadata.ContentBlockAddress}"

    let private normalizeState (state: DedupeIndexState) =
        if isNull (box state) then
            DedupeIndexState.Empty
        else
            {
                Records = if isNull state.Records then Array.empty else state.Records
                FinalizedManifests =
                    if isNull state.FinalizedManifests then
                        Array.empty
                    else
                        state.FinalizedManifests
                MetadataRecords = if isNull state.MetadataRecords then Array.empty else state.MetadataRecords
            }

    let private recordsDictionary state =
        let map = Dictionary<string, DedupeIndexRecord>()

        for record in (normalizeState state).Records do
            if not (isNull (box record)) then map[recordKey record] <- record

        map

    let private finalizedManifestDictionary state =
        let map = Dictionary<string, RuntimeFinalizedManifestRegistration>()

        for registration in (normalizeState state).FinalizedManifests do
            if not (isNull (box registration)) then
                map[runtimeFinalizedManifestKey registration] <- registration

        map

    let private metadataDictionary state =
        let map = Dictionary<string, ContentBlockMetadata>()

        for metadata in (normalizeState state).MetadataRecords do
            if
                not (isNull (box metadata))
                && not (String.IsNullOrWhiteSpace metadata.ContentBlockAddress)
            then
                map[metadataKey metadata] <- metadata

        map

    let private materializeState
        (records: Dictionary<string, DedupeIndexRecord>)
        (finalizedManifests: Dictionary<string, RuntimeFinalizedManifestRegistration>)
        (metadataRecords: Dictionary<string, ContentBlockMetadata>)
        =
        {
            Records = records.Values |> Seq.toArray
            FinalizedManifests = finalizedManifests.Values |> Seq.toArray
            MetadataRecords = metadataRecords.Values |> Seq.toArray
        }

    let private manifestContainsBlock contentBlockAddress (manifest: FileManifest) =
        not (isNull (box manifest))
        && not (isNull manifest.Blocks)
        && manifest.Blocks
           |> Seq.exists (fun block ->
               not (isNull (box block))
               && block.Address = contentBlockAddress)

    let private metadataByAddress (metadata: ContentBlockMetadata array) =
        let map = Dictionary<ContentBlockAddress, ContentBlockMetadata>()

        if not (isNull metadata) then
            for item in metadata do
                if
                    not (isNull (box item))
                    && not (String.IsNullOrWhiteSpace item.ContentBlockAddress)
                then
                    map[item.ContentBlockAddress] <- item

        map

    let private payloadByAddress (blockPayloads: FinalizeManifestBlockPayload array) =
        let map = Dictionary<ContentBlockAddress, byte array>()

        if not (isNull blockPayloads) then
            for payload in blockPayloads do
                if
                    not (isNull (box payload))
                    && not (String.IsNullOrWhiteSpace payload.Address)
                    && not (isNull payload.Payload)
                then
                    map[payload.Address] <- payload.Payload

        map

    let private isFinalizedForManifest (source: FinalizedManifestIndexSource) =
        not (isNull (box source.Session))
        && not (isNull (box source.Manifest))
        && source.Session.FinalizedManifestAddress = Some source.Manifest.ManifestAddress

    let private tryDecodeBlock payload =
        match ContentBlockFormat.decode payload with
        | Ok decodedBlock -> Some decodedBlock
        | Error _ -> None

    let private manifestBlocks (manifest: FileManifest) =
        if isNull (box manifest) || isNull manifest.Blocks then
            None
        else
            let blocks = manifest.Blocks |> Seq.toArray

            if blocks.Length = 0
               || blocks
                  |> Array.exists (fun block -> isNull (box block)) then
                None
            else
                Some blocks

    let private tryCreateRuntimeRegistration (registration: FinalizedManifestRegistration) =
        if isNull (box registration.Session)
           || isNull (box registration.Manifest)
           || registration.Session.FinalizedManifestAddress
              <> Some registration.Manifest.ManifestAddress then
            None
        else
            match manifestBlocks registration.Manifest with
            | None -> None
            | Some manifestBlocks ->
                let payloads = payloadByAddress registration.BlockPayloads
                let blocks = ResizeArray<RegisteredContentBlock>()

                for block in manifestBlocks do
                    match payloads.TryGetValue block.Address with
                    | true, payload ->
                        match tryDecodeBlock payload with
                        | Some decodedBlock when decodedBlock.Address = block.Address ->
                            blocks.Add
                                {
                                    Address = block.Address
                                    ChunkAddresses =
                                        decodedBlock.Chunks
                                        |> Array.map (fun chunk -> chunk.Address)
                                }
                        | _ -> ()
                    | _ -> ()

                if blocks.Count <> manifestBlocks.Length then
                    None
                else
                    Some
                        {
                            StoragePoolId = registration.StoragePoolId
                            Session = registration.Session
                            ManifestAddress = registration.Manifest.ManifestAddress
                            Blocks = blocks.ToArray()
                        }

    let private rangeEnd (range: ContentBlockMetadataRange) =
        if range.OrdinalCount > Int32.MaxValue - range.OrdinalStart then
            Int32.MaxValue
        else
            range.OrdinalStart + range.OrdinalCount

    let private tryCreateRecordFromChunkAddresses
        storagePoolId
        manifestAddress
        contentBlockAddress
        (metadata: ContentBlockMetadata)
        (chunkAddresses: ChunkAddress array)
        range
        =
        if metadata.StoragePoolId <> storagePoolId
           || metadata.ContentBlockAddress
              <> contentBlockAddress
           || range.ActiveManifestCount <= 0
           || range.OrdinalStart < 0
           || range.OrdinalCount < MinimumAcceptedReuseRunLength
           || rangeEnd range > chunkAddresses.Length then
            None
        else
            let ordinalCount = Math.Min(range.OrdinalCount, MaxWindowChunks)

            let protectedChunkAddresses =
                chunkAddresses
                |> Array.skip range.OrdinalStart
                |> Array.truncate ordinalCount
                |> Array.map (protectChunkAddress storagePoolId)

            if protectedChunkAddresses.Length < MinimumAcceptedReuseRunLength then
                None
            else
                Some
                    {
                        StoragePoolId = storagePoolId
                        ManifestAddress = manifestAddress
                        ContentBlockAddress = contentBlockAddress
                        OrdinalStart = range.OrdinalStart
                        OrdinalCount = protectedChunkAddresses.Length
                        MetadataVersion = metadata.MetadataVersion
                        ProtectedChunkAddresses = protectedChunkAddresses
                    }

    let recordsAfterFinalize (source: FinalizedManifestIndexSource) =
        if not (isFinalizedForManifest source) then
            Array.empty
        else
            let metadata = metadataByAddress source.Metadata
            let payloads = payloadByAddress source.BlockPayloads
            let output = ResizeArray<DedupeIndexRecord>()

            if not (isNull source.Manifest.Blocks) then
                for block in source.Manifest.Blocks do
                    if not (isNull (box block)) then
                        match metadata.TryGetValue block.Address, payloads.TryGetValue block.Address with
                        | (true, blockMetadata), (true, payload) ->
                            match tryDecodeBlock payload with
                            | Some decodedBlock ->
                                if
                                    decodedBlock.Address = block.Address
                                    && not (isNull blockMetadata.Ranges)
                                then
                                    let chunkAddresses =
                                        decodedBlock.Chunks
                                        |> Array.map (fun chunk -> chunk.Address)

                                    for range in blockMetadata.Ranges do
                                        match
                                            tryCreateRecordFromChunkAddresses
                                                source.StoragePoolId
                                                source.Manifest.ManifestAddress
                                                block.Address
                                                blockMetadata
                                                chunkAddresses
                                                range
                                            with
                                        | Some record -> output.Add(record)
                                        | None -> ()
                            | None -> ()
                        | _ -> ()

            output.ToArray()

    let private isSameCandidateWindow (left: DedupeIndexRecord) (right: DedupeIndexRecord) =
        left.StoragePoolId = right.StoragePoolId
        && left.ManifestAddress = right.ManifestAddress
        && left.ContentBlockAddress = right.ContentBlockAddress
        && left.OrdinalStart = right.OrdinalStart
        && left.OrdinalCount = right.OrdinalCount

    let private writeRecords (records: Dictionary<string, DedupeIndexRecord>) (newRecords: DedupeIndexRecord array) =
        for record in newRecords do
            let hasNewerRecord =
                records.Values
                |> Seq.exists (fun existing ->
                    isSameCandidateWindow existing record
                    && existing.MetadataVersion > record.MetadataVersion)

            if not hasNewerRecord then
                records.Values
                |> Seq.filter (fun existing ->
                    isSameCandidateWindow existing record
                    && existing.MetadataVersion <= record.MetadataVersion)
                |> Seq.map recordKey
                |> Seq.toArray
                |> Array.iter (fun key -> records.Remove key |> ignore)

                records[recordKey record] <- record

    let private removeRecordsForMetadataBlock (records: Dictionary<string, DedupeIndexRecord>) storagePoolId contentBlockAddress metadataVersion =
        records.Values
        |> Seq.filter (fun existing ->
            existing.StoragePoolId = storagePoolId
            && existing.ContentBlockAddress = contentBlockAddress
            && existing.MetadataVersion <= metadataVersion)
        |> Seq.map recordKey
        |> Seq.toArray
        |> Array.iter (fun key -> records.Remove key |> ignore)

    let rebuild sources =
        let records = Dictionary<string, DedupeIndexRecord>()

        if not (isNull sources) then
            sources
            |> Array.collect recordsAfterFinalize
            |> writeRecords records

        records.Values |> Seq.toArray

    let private runtimeRegistrationMatchesMetadata (metadata: ContentBlockMetadata) (registration: RuntimeFinalizedManifestRegistration) =
        registration.StoragePoolId = metadata.StoragePoolId
        && (registration.Blocks
            |> Array.exists (fun block -> block.Address = metadata.ContentBlockAddress))

    let private writeForRegistrationWithMetadata
        (records: Dictionary<string, DedupeIndexRecord>)
        (registration: RuntimeFinalizedManifestRegistration)
        (metadata: ContentBlockMetadata)
        =
        let output = ResizeArray<DedupeIndexRecord>()

        removeRecordsForMetadataBlock records registration.StoragePoolId metadata.ContentBlockAddress metadata.MetadataVersion

        if not (isNull metadata.Ranges) then
            for block in registration.Blocks do
                if block.Address = metadata.ContentBlockAddress then
                    for range in metadata.Ranges do
                        match
                            tryCreateRecordFromChunkAddresses
                                registration.StoragePoolId
                                registration.ManifestAddress
                                block.Address
                                metadata
                                block.ChunkAddresses
                                range
                            with
                        | Some record -> output.Add(record)
                        | None -> ()

        let newRecords = output.ToArray()
        writeRecords records newRecords
        newRecords

    let replaceAllInState state (newRecords: DedupeIndexRecord array) =
        let records = Dictionary<string, DedupeIndexRecord>()
        writeRecords records newRecords

        let normalized = normalizeState state

        { normalized with Records = records.Values |> Seq.toArray }

    let writeAfterFinalizeInState state (source: FinalizedManifestIndexSource) =
        let normalized = normalizeState state
        let records = recordsDictionary normalized
        let finalizedManifests = finalizedManifestDictionary normalized
        let metadataRecords = metadataDictionary normalized
        let newRecords = recordsAfterFinalize source
        writeRecords records newRecords

        materializeState records finalizedManifests metadataRecords, newRecords

    let writeAfterAuthoritativeMetadataInState state (metadata: ContentBlockMetadata) =
        let normalized = normalizeState state

        if
            isNull (box metadata)
            || String.IsNullOrWhiteSpace metadata.ContentBlockAddress
        then
            normalized, Array.empty
        else
            let records = recordsDictionary normalized
            let finalizedManifests = finalizedManifestDictionary normalized
            let metadataRecords = metadataDictionary normalized
            metadataRecords[metadataKey metadata] <- metadata
            removeRecordsForMetadataBlock records metadata.StoragePoolId metadata.ContentBlockAddress metadata.MetadataVersion

            let matchingRegistrations =
                finalizedManifests
                |> Seq.filter (fun kvp -> runtimeRegistrationMatchesMetadata metadata kvp.Value)
                |> Seq.toArray

            let newRecords =
                matchingRegistrations
                |> Seq.collect (fun kvp -> writeForRegistrationWithMetadata records kvp.Value metadata)
                |> Seq.toArray

            materializeState records finalizedManifests metadataRecords, newRecords

    let registerFinalizedManifestInState state (registration: FinalizedManifestRegistration) =
        let normalized = normalizeState state

        match tryCreateRuntimeRegistration registration with
        | None -> normalized, Array.empty
        | Some runtimeRegistration ->
            let records = recordsDictionary normalized
            let finalizedManifests = finalizedManifestDictionary normalized
            let metadataRecords = metadataDictionary normalized
            let key = finalizedManifestKey registration
            finalizedManifests[key] <- runtimeRegistration

            let newRecords =
                metadataRecords.Values
                |> Seq.filter (fun metadata -> runtimeRegistrationMatchesMetadata metadata runtimeRegistration)
                |> Seq.collect (writeForRegistrationWithMetadata records runtimeRegistration)
                |> Seq.toArray

            materializeState records finalizedManifests metadataRecords, newRecords

    let replaceAll (newRecords: DedupeIndexRecord array) = lock globalGate (fun () -> globalState <- replaceAllInState globalState newRecords)

    let writeAfterFinalize (source: FinalizedManifestIndexSource) =
        lock globalGate (fun () ->
            let nextState, newRecords = writeAfterFinalizeInState globalState source
            globalState <- nextState
            newRecords)

    let writeAfterAuthoritativeMetadata (metadata: ContentBlockMetadata) =
        lock globalGate (fun () ->
            let nextState, newRecords = writeAfterAuthoritativeMetadataInState globalState metadata
            globalState <- nextState
            newRecords)

    let registerFinalizedManifest (registration: FinalizedManifestRegistration) =
        lock globalGate (fun () ->
            let nextState, newRecords = registerFinalizedManifestInState globalState registration
            globalState <- nextState
            newRecords)

    let snapshot () = lock globalGate (fun () -> Array.copy (normalizeState globalState).Records)

    let finalizedManifestContainsBlock storagePoolId manifestAddress contentBlockAddress (state: DedupeIndexState) =
        (normalizeState state).FinalizedManifests
        |> Array.exists (fun registration ->
            not (isNull (box registration))
            && registration.StoragePoolId = storagePoolId
            && registration.ManifestAddress = manifestAddress
            && not (isNull registration.Blocks)
            && registration.Blocks
               |> Array.exists (fun block ->
                   not (isNull (box block))
                   && block.Address = contentBlockAddress))

    let private normalizeScopePath (path: RelativePath) =
        let normalized = (Utilities.normalizeFilePath $"{path}").Trim()

        if String.IsNullOrWhiteSpace normalized then
            String.Empty
        elif normalized = "/" then
            "/"
        else
            let withLeadingSlash =
                if normalized.StartsWith("/", StringComparison.Ordinal) then
                    normalized
                else
                    $"/{normalized}"

            withLeadingSlash.TrimEnd('/')

    let private finalizedScopeContainsRequestedScope finalizedScope requestedScope =
        let finalizedScope = normalizeScopePath finalizedScope
        let requestedScope = normalizeScopePath requestedScope

        if String.IsNullOrWhiteSpace finalizedScope
           || String.IsNullOrWhiteSpace requestedScope then
            false
        elif finalizedScope = "/" then
            true
        elif String.Equals(finalizedScope, requestedScope, StringComparison.Ordinal) then
            true
        else
            requestedScope.StartsWith($"{finalizedScope}/", StringComparison.Ordinal)

    let finalizedScopedManifestContainsBlock storagePoolId repositoryId authorizedScope manifestAddress contentBlockAddress (state: DedupeIndexState) =
        (normalizeState state).FinalizedManifests
        |> Array.exists (fun registration ->
            not (isNull (box registration))
            && registration.StoragePoolId = storagePoolId
            && registration.ManifestAddress = manifestAddress
            && not (isNull (box registration.Session))
            && registration.Session.RepositoryId = repositoryId
            && finalizedScopeContainsRequestedScope registration.Session.AuthorizedScope authorizedScope
            && not (isNull registration.Blocks)
            && registration.Blocks
               |> Array.exists (fun block ->
                   not (isNull (box block))
                   && block.Address = contentBlockAddress))

    let tryFindFinalizedScopedContentBlockMetadata storagePoolId repositoryId authorizedScope manifestAddress contentBlockAddress (state: DedupeIndexState) =
        if finalizedScopedManifestContainsBlock storagePoolId repositoryId authorizedScope manifestAddress contentBlockAddress state
           |> not then
            None
        else
            (normalizeState state).MetadataRecords
            |> Array.tryFind (fun metadata ->
                not (isNull (box metadata))
                && metadata.StoragePoolId = storagePoolId
                && metadata.ContentBlockAddress = contentBlockAddress)

    let private candidateFromRecord matchingKeyChunkCount (record: DedupeIndexRecord) =
        {
            StoragePoolId = record.StoragePoolId
            ManifestAddress = record.ManifestAddress
            ContentBlockAddress = record.ContentBlockAddress
            OrdinalStart = record.OrdinalStart
            OrdinalCount = record.OrdinalCount
            MetadataVersion = record.MetadataVersion
            MatchingKeyChunkCount = matchingKeyChunkCount
            ProtectedChunkAddresses = Array.copy record.ProtectedChunkAddresses
        }

    let private selectedCandidates (storagePoolId: StoragePoolId) (requested: ChunkAddress array) (records: DedupeIndexRecord array) =
        let requestedTokens =
            requested
            |> Array.map (protectChunkAddress storagePoolId)
            |> HashSet<string>

        let maxCandidateWindows = Math.Min(records |> Array.length, requested.Length * MaxCandidateWindowsPerKeyChunk)

        let candidates =
            records
            |> Array.choose (fun record ->
                if record.StoragePoolId <> storagePoolId then
                    None
                else
                    let matchingKeyChunkCount =
                        record.ProtectedChunkAddresses
                        |> Array.filter requestedTokens.Contains
                        |> Array.distinct
                        |> Array.length

                    if matchingKeyChunkCount = 0 then
                        None
                    else
                        Some(candidateFromRecord matchingKeyChunkCount record))
            |> Array.sortBy (fun candidate ->
                -candidate.MatchingKeyChunkCount, candidate.StoragePoolId, candidate.ContentBlockAddress, candidate.OrdinalStart, -candidate.MetadataVersion)

        let output = ResizeArray<ContentBlockDiscoveryCandidate>()
        let mutable protectedChunkBudget = MaxResponseProtectedChunks
        let mutable index = 0

        while index < candidates.Length
              && output.Count < maxCandidateWindows
              && protectedChunkBudget > 0 do
            let candidate = candidates[index]

            if candidate.ProtectedChunkAddresses.Length
               <= protectedChunkBudget then
                output.Add(candidate)

                protectedChunkBudget <-
                    protectedChunkBudget
                    - candidate.ProtectedChunkAddresses.Length

            index <- index + 1

        output.ToArray(), candidates.Length > output.Count

    let discover (storagePoolId: StoragePoolId) (keyChunkAddresses: ChunkAddress array) (_now: Instant) (records: DedupeIndexRecord array) =
        let requested = if isNull keyChunkAddresses then Array.empty else keyChunkAddresses

        let accepted =
            requested
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.distinct
            |> Array.truncate MaxDiscoveryKeyChunkAddresses

        let candidates, candidatesPartial =
            if accepted.Length = 0
               || isNull records
               || records.Length = 0 then
                Array.empty, false
            else
                selectedCandidates storagePoolId accepted records

        let message =
            if candidates.Length = 0 then
                "No positive ContentBlock candidates are available. Empty discovery results are non-authoritative and do not prove absence."
            else
                "ContentBlock candidates are bounded, response-protected, and non-authoritative. Reuse still requires an authoritative range claim."

        {
            RequestedKeyChunkCount = requested.Length
            AcceptedKeyChunkCount = accepted.Length
            Policy = discoveryPolicy ()
            CandidateContentBlocks = candidates
            IsPartial =
                candidates.Length = 0
                || requested.Length > accepted.Length
                || candidatesPartial
            Message = message
        }

    let toReuseRangeHint (candidate: ContentBlockDiscoveryCandidate) =
        {
            StoragePoolId = candidate.StoragePoolId
            ContentBlockAddress = candidate.ContentBlockAddress
            OrdinalStart = candidate.OrdinalStart
            OrdinalCount = candidate.OrdinalCount
            MetadataVersion = candidate.MetadataVersion
        }
