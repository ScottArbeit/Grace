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

/// Contains dedupe index helpers.
module DedupeIndex =

    /// Represents finalized manifest index source.
    type FinalizedManifestIndexSource =
        {
            StoragePoolId: StoragePoolId
            Session: UploadSessionDto
            Manifest: FileManifest
            BlockPayloads: FinalizeManifestBlockPayload array
            Metadata: ContentBlockMetadata array
        }

    /// Represents dedupe index record.
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

    /// Represents finalized manifest registration.
    type FinalizedManifestRegistration =
        {
            StoragePoolId: StoragePoolId
            Session: UploadSessionDto
            Manifest: FileManifest
            BlockPayloads: FinalizeManifestBlockPayload array
        }

    /// Represents the registered content block contract.
    type RegisteredContentBlock = { Address: ContentBlockAddress; ChunkAddresses: ChunkAddress array }

    /// Represents runtime finalized manifest registration.
    type RuntimeFinalizedManifestRegistration =
        {
            StoragePoolId: StoragePoolId
            Session: UploadSessionDto
            ManifestAddress: ManifestAddress
            Blocks: RegisteredContentBlock array
        }

    /// Represents dedupe index state.
    type DedupeIndexState =
        {
            Records: DedupeIndexRecord array
            FinalizedManifests: RuntimeFinalizedManifestRegistration array
            MetadataRecords: ContentBlockMetadata array
        }

        /// Represents the normalized empty instance used before persisted state or caller input contributes values.
        static member Empty = { Records = Array.empty; FinalizedManifests = Array.empty; MetadataRecords = Array.empty }

    let private globalGate = obj ()
    let mutable private globalState = DedupeIndexState.Empty
    let private MinimumIssuedReuseRunLength = 1

    /// Builds the bounded, non-authoritative discovery policy returned with dedupe candidate responses.
    let discoveryPolicy () : ContentBlockDiscoveryPolicy =
        {
            MaxKeyChunkAddresses = MaxDiscoveryKeyChunkAddresses
            MaxCandidateWindowsPerKeyChunk = MaxCandidateWindowsPerKeyChunk
            MaxWindowChunks = MaxWindowChunks
            MaxResponseProtectedChunks = MaxResponseProtectedChunks
            ResponseTtlSeconds = ResponseTtlSeconds
            MinimumAcceptedReuseRunLength = MinimumIssuedReuseRunLength
            PositiveCandidatesEnabled = true
            EmptyResponseMeansAbsent = false
            IsAuthoritative = false
        }

    /// Derives the repository-scoped dedupe StoragePool identifier from the repository id.
    let storagePoolIdForRepositoryId (repositoryId: RepositoryId) = StoragePoolRouting.repositoryDedupeStoragePoolId repositoryId

    /// Reads the configured repository StoragePool id and fails closed when routing is unsupported.
    let storagePoolIdForRepository (repositoryDto: RepositoryDto) =
        if isNull (box repositoryDto) then
            invalidOp "Repository state is required before resolving a StoragePool route."
        elif String.IsNullOrWhiteSpace repositoryDto.StoragePoolId then
            invalidOp "Repository StoragePoolId is not configured."
        elif repositoryDto.StoragePoolId
             <> StoragePoolRouting.defaultStoragePoolId then
            invalidOp $"StoragePoolId '{repositoryDto.StoragePoolId}' is not configured. StoragePool routing fails closed."
        else
            repositoryDto.StoragePoolId

    /// Hashes a chunk address with the StoragePool id before exposing it in dedupe discovery responses.
    let private protectChunkAddress (storagePoolId: StoragePoolId) (chunkAddress: ChunkAddress) =
        let preimage = $"grace.dedupe-index.v1.protected-window\n{storagePoolId}\n{chunkAddress}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage))
        $"protected-sha256:{Convert.ToHexString(hash).ToLowerInvariant()}"

    /// Builds the unique in-memory key for one dedupe record window and metadata version.
    let private recordKey (record: DedupeIndexRecord) =
        $"{record.StoragePoolId}|{record.ManifestAddress}|{record.ContentBlockAddress}|{record.OrdinalStart}|{record.OrdinalCount}|{record.MetadataVersion}"

    /// Builds the repository/upload-session key that scopes finalized manifest registrations.
    let private finalizedSessionScopeKey (session: UploadSessionDto) = $"{session.RepositoryId:N}|{session.UploadSessionId:N}"

    /// Builds the persisted finalized-manifest registration key from pool, session, and manifest address.
    let private finalizedManifestKey (registration: FinalizedManifestRegistration) =
        $"{registration.StoragePoolId}|{finalizedSessionScopeKey registration.Session}|{registration.Manifest.ManifestAddress}"

    /// Builds the persisted finalized-manifest registration key from pool, session, and manifest address.
    let private runtimeFinalizedManifestKey (registration: RuntimeFinalizedManifestRegistration) =
        $"{registration.StoragePoolId}|{finalizedSessionScopeKey registration.Session}|{registration.ManifestAddress}"

    /// Builds the content-block metadata key from StoragePool id and content block address.
    let private metadataKey (metadata: ContentBlockMetadata) = $"{metadata.StoragePoolId}|{metadata.ContentBlockAddress}"

    /// Normalizes state.
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

    /// Indexes normalized dedupe records by their stable record key for replacement.
    let private recordsDictionary state =
        let map = Dictionary<string, DedupeIndexRecord>()

        for record in (normalizeState state).Records do
            if not (isNull (box record)) then map[recordKey record] <- record

        map

    /// Indexes normalized finalized manifest registrations by their runtime key.
    let private finalizedManifestDictionary state =
        let map = Dictionary<string, RuntimeFinalizedManifestRegistration>()

        for registration in (normalizeState state).FinalizedManifests do
            if not (isNull (box registration)) then
                map[runtimeFinalizedManifestKey registration] <- registration

        map

    /// Indexes authoritative content block metadata by StoragePool id and content block address.
    let private metadataDictionary state =
        let map = Dictionary<string, ContentBlockMetadata>()

        for metadata in (normalizeState state).MetadataRecords do
            if
                not (isNull (box metadata))
                && not (String.IsNullOrWhiteSpace metadata.ContentBlockAddress)
            then
                map[metadataKey metadata] <- metadata

        map

    /// Converts the working dictionaries back into immutable dedupe index state arrays.
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

    /// Checks whether a file manifest declares the requested content block address.
    let private manifestContainsBlock contentBlockAddress (manifest: FileManifest) =
        not (isNull (box manifest))
        && not (isNull manifest.Blocks)
        && manifest.Blocks
           |> Seq.exists (fun block ->
               not (isNull (box block))
               && block.Address = contentBlockAddress)

    /// Indexes supplied content block metadata by content block address.
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

    /// Indexes supplied finalize payloads by content block address.
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

    /// Checks whether an upload session finalized exactly the manifest being indexed.
    let private isFinalizedForManifest (source: FinalizedManifestIndexSource) =
        not (isNull (box source.Session))
        && not (isNull (box source.Manifest))
        && source.Session.FinalizedManifestAddress = Some source.Manifest.ManifestAddress

    /// Attempts to decode block.
    let private tryDecodeBlock payload =
        match ContentBlockFormat.decode payload with
        | Ok decodedBlock -> Some decodedBlock
        | Error _ -> None

    /// Returns non-null manifest blocks only when the manifest contains a complete block list.
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

    /// Attempts to create runtime registration.
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

    /// Computes an ordinal range end with saturation to avoid integer overflow.
    let private rangeEnd (range: ContentBlockMetadataRange) =
        if range.OrdinalCount > Int32.MaxValue - range.OrdinalStart then
            Int32.MaxValue
        else
            range.OrdinalStart + range.OrdinalCount

    /// Clamps the accepted reuse-run length to the available chunk count.
    let private minimumAcceptedOrdinalCountForBlock chunkAddressCount =
        if chunkAddressCount <= 0 then
            MinimumAcceptedReuseRunLength
        else
            Math.Min(MinimumAcceptedReuseRunLength, chunkAddressCount)

    /// Attempts to create record from chunk addresses.
    let private tryCreateRecordFromChunkAddresses
        storagePoolId
        manifestAddress
        contentBlockAddress
        (metadata: ContentBlockMetadata)
        (chunkAddresses: ChunkAddress array)
        range
        =
        let minimumAcceptedOrdinalCount =
            if isNull chunkAddresses then
                MinimumAcceptedReuseRunLength
            else
                minimumAcceptedOrdinalCountForBlock chunkAddresses.Length

        if metadata.StoragePoolId <> storagePoolId
           || metadata.ContentBlockAddress
              <> contentBlockAddress
           || range.ActiveManifestCount <= 0
           || range.OrdinalStart < 0
           || range.OrdinalCount < minimumAcceptedOrdinalCount
           || isNull chunkAddresses
           || rangeEnd range > chunkAddresses.Length then
            None
        else
            let ordinalCount = Math.Min(range.OrdinalCount, MaxWindowChunks)

            let protectedChunkAddresses =
                chunkAddresses
                |> Array.skip range.OrdinalStart
                |> Array.truncate ordinalCount
                |> Array.map (protectChunkAddress storagePoolId)

            if protectedChunkAddresses.Length < minimumAcceptedOrdinalCount then
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

    /// Splits an active metadata range into bounded candidate windows for discovery responses.
    let private splitRangeIntoDedupeWindows minimumAcceptedOrdinalCount (range: ContentBlockMetadataRange) =
        let output = ResizeArray<ContentBlockMetadataRange>()

        /// Interpolates a physical byte offset for an ordinal inside a metadata range.
        let physicalOffsetForOrdinal ordinalStart =
            let ordinalDelta = ordinalStart - range.OrdinalStart

            if ordinalDelta <= 0 then
                range.PhysicalOffset
            elif ordinalDelta >= range.OrdinalCount then
                range.PhysicalOffset + range.PhysicalLength
            else
                range.PhysicalOffset
                + int64 (
                    decimal range.PhysicalLength
                    * decimal ordinalDelta
                    / decimal range.OrdinalCount
                )

        if not (isNull (box range))
           && range.ActiveManifestCount > 0
           && range.OrdinalStart >= 0
           && range.OrdinalCount >= minimumAcceptedOrdinalCount
           && range.PhysicalOffset >= 0L
           && range.PhysicalLength > 0L then
            let mutable remainingOrdinalCount = range.OrdinalCount
            let mutable currentOrdinalStart = range.OrdinalStart
            let mutable currentPhysicalOffset = range.PhysicalOffset
            let mutable remainingPhysicalLength = range.PhysicalLength

            while remainingOrdinalCount
                  >= minimumAcceptedOrdinalCount
                  && remainingPhysicalLength > 0L do
                let windowOrdinalCount = Math.Min(remainingOrdinalCount, MaxWindowChunks)

                let windowPhysicalLength =
                    if windowOrdinalCount = remainingOrdinalCount then
                        remainingPhysicalLength
                    else
                        let proportionalLength =
                            decimal range.PhysicalLength
                            * decimal windowOrdinalCount
                            / decimal range.OrdinalCount
                            |> Math.Ceiling
                            |> int64

                        Math.Min(remainingPhysicalLength, Math.Max(1L, proportionalLength))

                output.Add
                    { range with
                        OrdinalStart = currentOrdinalStart
                        OrdinalCount = windowOrdinalCount
                        PhysicalOffset = currentPhysicalOffset
                        PhysicalLength = windowPhysicalLength
                    }

                remainingOrdinalCount <- remainingOrdinalCount - windowOrdinalCount
                currentOrdinalStart <- currentOrdinalStart + windowOrdinalCount
                currentPhysicalOffset <- currentPhysicalOffset + windowPhysicalLength
                remainingPhysicalLength <- remainingPhysicalLength - windowPhysicalLength

            if remainingOrdinalCount > 0 && output.Count > 0 then
                let tailOrdinalStart = Math.Max(range.OrdinalStart, rangeEnd range - MaxWindowChunks)

                let tailPhysicalOffset = physicalOffsetForOrdinal tailOrdinalStart

                let tailPhysicalLength =
                    Math.Max(
                        1L,
                        range.PhysicalOffset + range.PhysicalLength
                        - tailPhysicalOffset
                    )

                if output
                   |> Seq.exists (fun existing ->
                       existing.OrdinalStart = tailOrdinalStart
                       && existing.OrdinalCount = MaxWindowChunks)
                   |> not then
                    output.Add
                        { range with
                            OrdinalStart = tailOrdinalStart
                            OrdinalCount = MaxWindowChunks
                            PhysicalOffset = tailPhysicalOffset
                            PhysicalLength = tailPhysicalLength
                        }

        output.ToArray()

    /// Combines adjacent active metadata ranges into one candidate chain.
    let private mergeActiveChain (ranges: ContentBlockMetadataRange list) =
        let ordered = ranges |> List.rev
        let first = ordered.Head

        { first with
            OrdinalCount =
                ordered
                |> List.sumBy (fun range -> range.OrdinalCount)
            ActiveManifestCount =
                ordered
                |> List.map (fun range -> range.ActiveManifestCount)
                |> List.min
            PhysicalLength =
                ordered
                |> List.sumBy (fun range -> range.PhysicalLength)
        }

    /// Selects active contiguous ranges that can safely contribute dedupe windows.
    let private contiguousActiveRanges chunkAddressCount (ranges: ContentBlockMetadataRange array) =
        let output = ResizeArray<ContentBlockMetadataRange>()
        let minimumAcceptedOrdinalCount = minimumAcceptedOrdinalCountForBlock chunkAddressCount

        if not (isNull ranges) then
            let sortedRanges =
                ranges
                |> Array.filter (fun range ->
                    not (isNull (box range))
                    && range.ActiveManifestCount > 0
                    && range.OrdinalStart >= 0
                    && range.OrdinalCount > 0
                    && range.PhysicalOffset >= 0L
                    && range.PhysicalLength > 0L)
                |> Array.sortBy (fun range -> range.OrdinalStart, range.PhysicalOffset, range.PhysicalLength)

            /// Computes the exclusive physical byte end for a metadata range with overflow protection.
            let rangePhysicalEnd (range: ContentBlockMetadataRange) =
                if range.PhysicalLength > Int64.MaxValue - range.PhysicalOffset then
                    Int64.MaxValue
                else
                    range.PhysicalOffset + range.PhysicalLength

            let rangesByStart = Dictionary<int * int64, ResizeArray<ContentBlockMetadataRange>>()
            let rangesWithPredecessors = HashSet<int * int64>()

            for range in sortedRanges do
                let startKey = range.OrdinalStart, range.PhysicalOffset

                match rangesByStart.TryGetValue startKey with
                | true, ranges -> ranges.Add range
                | false, _ ->
                    let ranges = ResizeArray<ContentBlockMetadataRange>()
                    ranges.Add range
                    rangesByStart[startKey] <- ranges

                rangesWithPredecessors.Add(rangeEnd range, rangePhysicalEnd range)
                |> ignore

            /// Expands forward-linked active ranges into candidate chains.
            let rec collectChains (chain: ContentBlockMetadataRange list) =
                let current = chain.Head
                let activeChain = mergeActiveChain chain
                let currentEnd = rangeEnd current
                let currentPhysicalEnd = rangePhysicalEnd current

                let nextRanges =
                    let nextKey = currentEnd, currentPhysicalEnd

                    match rangesByStart.TryGetValue nextKey with
                    | true, ranges ->
                        ranges
                        |> Seq.filter (fun candidate ->
                            candidate.OrdinalCount
                            <= Int32.MaxValue - activeChain.OrdinalCount
                            && candidate.PhysicalLength
                               <= Int64.MaxValue - activeChain.PhysicalLength)
                        |> Seq.toArray
                    | false, _ -> Array.empty

                if nextRanges.Length = 0 then
                    output.Add(mergeActiveChain chain)
                else
                    for nextRange in nextRanges do
                        collectChains (nextRange :: chain)

            /// Identifies ranges already reachable from an earlier active range.
            let hasPredecessor (range: ContentBlockMetadataRange) =
                let startKey = range.OrdinalStart, range.PhysicalOffset

                rangesWithPredecessors.Contains startKey

            for range in sortedRanges do
                if not (hasPredecessor range) then collectChains [ range ]

        output
        |> Seq.distinctBy (fun range -> range.OrdinalStart, range.OrdinalCount, range.PhysicalOffset, range.PhysicalLength)
        |> Seq.collect (splitRangeIntoDedupeWindows minimumAcceptedOrdinalCount)
        |> Seq.distinctBy (fun range -> range.OrdinalStart, range.OrdinalCount, range.PhysicalOffset, range.PhysicalLength)
        |> Seq.toArray

    /// Derives dedupe records from a finalized manifest, decoded block payloads, and authoritative metadata.
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

                                    for range in contiguousActiveRanges chunkAddresses.Length blockMetadata.Ranges do
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

    /// Compares candidate window identity while ignoring metadata version freshness.
    let private isSameCandidateWindow (left: DedupeIndexRecord) (right: DedupeIndexRecord) =
        left.StoragePoolId = right.StoragePoolId
        && left.ManifestAddress = right.ManifestAddress
        && left.ContentBlockAddress = right.ContentBlockAddress
        && left.OrdinalStart = right.OrdinalStart
        && left.OrdinalCount = right.OrdinalCount

    /// Upserts dedupe records while replacing older metadata versions for the same candidate window.
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

    /// Drops stale candidate windows for a content block when newer authoritative metadata arrives.
    let private removeRecordsForMetadataBlock (records: Dictionary<string, DedupeIndexRecord>) storagePoolId contentBlockAddress metadataVersion =
        records.Values
        |> Seq.filter (fun existing ->
            existing.StoragePoolId = storagePoolId
            && existing.ContentBlockAddress = contentBlockAddress
            && existing.MetadataVersion <= metadataVersion)
        |> Seq.map recordKey
        |> Seq.toArray
        |> Array.iter (fun key -> records.Remove key |> ignore)

    /// Rebuilds dedupe records from finalized manifest sources.
    let rebuild sources =
        let records = Dictionary<string, DedupeIndexRecord>()

        if not (isNull sources) then
            sources
            |> Array.collect recordsAfterFinalize
            |> writeRecords records

        records.Values |> Seq.toArray

    /// Checks whether a finalized manifest registration contains the metadata content block.
    let private runtimeRegistrationMatchesMetadata (metadata: ContentBlockMetadata) (registration: RuntimeFinalizedManifestRegistration) =
        registration.StoragePoolId = metadata.StoragePoolId
        && (registration.Blocks
            |> Array.exists (fun block -> block.Address = metadata.ContentBlockAddress))

    /// Writes candidate windows produced by one finalized registration and matching metadata record.
    let private writeForRegistrationWithMetadata
        (records: Dictionary<string, DedupeIndexRecord>)
        (registration: RuntimeFinalizedManifestRegistration)
        (metadata: ContentBlockMetadata)
        =
        let output = ResizeArray<DedupeIndexRecord>()

        for block in registration.Blocks do
            if block.Address = metadata.ContentBlockAddress then
                let ranges = contiguousActiveRanges block.ChunkAddresses.Length metadata.Ranges

                if ranges.Length > 0 then
                    for range in ranges do
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

    /// Replaces state records with a normalized set while preserving registrations and metadata.
    let replaceAllInState state (newRecords: DedupeIndexRecord array) =
        let records = Dictionary<string, DedupeIndexRecord>()
        writeRecords records newRecords

        let normalized = normalizeState state

        { normalized with Records = records.Values |> Seq.toArray }

    /// Adds records produced by a finalized manifest into an explicit dedupe index state.
    let writeAfterFinalizeInState state (source: FinalizedManifestIndexSource) =
        let normalized = normalizeState state
        let records = recordsDictionary normalized
        let finalizedManifests = finalizedManifestDictionary normalized
        let metadataRecords = metadataDictionary normalized
        let newRecords = recordsAfterFinalize source
        writeRecords records newRecords

        materializeState records finalizedManifests metadataRecords, newRecords

    /// Refreshes metadata and recomputes matching candidate windows in an explicit dedupe index state.
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

    /// Stores a finalized manifest registration and computes records for metadata already known in state.
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

    /// Atomically replaces global dedupe records with the supplied normalized set.
    let replaceAll (newRecords: DedupeIndexRecord array) = lock globalGate (fun () -> globalState <- replaceAllInState globalState newRecords)

    /// Atomically adds global dedupe records produced by a finalized manifest.
    let writeAfterFinalize (source: FinalizedManifestIndexSource) =
        lock globalGate (fun () ->
            let nextState, newRecords = writeAfterFinalizeInState globalState source
            globalState <- nextState
            newRecords)

    /// Atomically refreshes global metadata and candidate windows for the content block.
    let writeAfterAuthoritativeMetadata (metadata: ContentBlockMetadata) =
        lock globalGate (fun () ->
            let nextState, newRecords = writeAfterAuthoritativeMetadataInState globalState metadata
            globalState <- nextState
            newRecords)

    /// Atomically records a finalized manifest for later metadata-driven dedupe indexing.
    let registerFinalizedManifest (registration: FinalizedManifestRegistration) =
        lock globalGate (fun () ->
            let nextState, newRecords = registerFinalizedManifestInState globalState registration
            globalState <- nextState
            newRecords)

    /// Returns a copy of the current global dedupe records.
    let snapshot () = lock globalGate (fun () -> Array.copy (normalizeState globalState).Records)

    /// Checks whether a file manifest declares the requested content block address.
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

    /// Normalizes scope path.
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

    /// Compares normalized authorized scopes before serving scoped finalized metadata.
    let private finalizedScopeMatchesRequestedScope finalizedScope requestedScope =
        let finalizedScope = normalizeScopePath finalizedScope
        let requestedScope = normalizeScopePath requestedScope

        if String.IsNullOrWhiteSpace finalizedScope
           || String.IsNullOrWhiteSpace requestedScope then
            false
        else
            String.Equals(finalizedScope, requestedScope, StringComparison.Ordinal)

    /// Checks whether a file manifest declares the requested content block address.
    let finalizedScopedManifestContainsBlock storagePoolId repositoryId authorizedScope manifestAddress contentBlockAddress (state: DedupeIndexState) =
        (normalizeState state).FinalizedManifests
        |> Array.exists (fun registration ->
            not (isNull (box registration))
            && registration.StoragePoolId = storagePoolId
            && registration.ManifestAddress = manifestAddress
            && not (isNull (box registration.Session))
            && registration.Session.RepositoryId = repositoryId
            && finalizedScopeMatchesRequestedScope registration.Session.AuthorizedScope authorizedScope
            && not (isNull registration.Blocks)
            && registration.Blocks
               |> Array.exists (fun block ->
                   not (isNull (box block))
                   && block.Address = contentBlockAddress))

    /// Attempts to find finalized scoped content block metadata.
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

    /// Projects an index record into a discovery candidate with the matching key-chunk count.
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

    /// Ranks bounded discovery candidates by protected key-chunk overlap and response budget.
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

    /// Returns bounded non-authoritative dedupe candidates for accepted key chunk addresses.
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

    /// Converts reuse range hint.
    let toReuseRangeHint (candidate: ContentBlockDiscoveryCandidate) =
        {
            StoragePoolId = candidate.StoragePoolId
            ContentBlockAddress = candidate.ContentBlockAddress
            OrdinalStart = candidate.OrdinalStart
            OrdinalCount = candidate.OrdinalCount
            MetadataVersion = candidate.MetadataVersion
        }
