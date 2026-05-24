namespace Grace.Shared

open Grace.Shared.Parameters.Storage
open Grace.Types.ContentBlockMetadata
open Grace.Types.Repository
open Grace.Types.Types
open Grace.Types.UploadSession
open NodaTime
open System
open System.Collections.Concurrent
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

    type private RegisteredContentBlock = { Address: ContentBlockAddress; ChunkAddresses: ChunkAddress array }

    type private RuntimeFinalizedManifestRegistration =
        {
            StoragePoolId: StoragePoolId
            Session: UploadSessionDto
            ManifestAddress: ManifestAddress
            Blocks: RegisteredContentBlock array
        }

    let private records = ConcurrentDictionary<string, DedupeIndexRecord>()
    let private finalizedManifests = ConcurrentDictionary<string, RuntimeFinalizedManifestRegistration>()
    let private metadataRecords = ConcurrentDictionary<string, ContentBlockMetadata>()

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

    let storagePoolIdForRepositoryId (repositoryId: RepositoryId) = StoragePoolId $"{repositoryId}"

    let storagePoolIdForRepository (repositoryDto: RepositoryDto) = storagePoolIdForRepositoryId repositoryDto.RepositoryId

    let private protectChunkAddress (storagePoolId: StoragePoolId) (chunkAddress: ChunkAddress) =
        let preimage = $"grace.dedupe-index.v1.protected-window{Environment.NewLine}{storagePoolId}{Environment.NewLine}{chunkAddress}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage))
        $"protected-sha256:{Convert.ToHexString(hash).ToLowerInvariant()}"

    let private recordKey (record: DedupeIndexRecord) =
        $"{record.StoragePoolId}|{record.ManifestAddress}|{record.ContentBlockAddress}|{record.OrdinalStart}|{record.OrdinalCount}|{record.MetadataVersion}"

    let private finalizedManifestKey (registration: FinalizedManifestRegistration) =
        $"{registration.StoragePoolId}|{registration.Session.UploadSessionId}|{registration.Manifest.ManifestAddress}"

    let private metadataKey (metadata: ContentBlockMetadata) = $"{metadata.StoragePoolId}|{metadata.ContentBlockAddress}"

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

    let private tryCreateRuntimeRegistration (registration: FinalizedManifestRegistration) =
        if isNull (box registration.Session)
           || isNull (box registration.Manifest)
           || registration.Session.FinalizedManifestAddress
              <> Some registration.Manifest.ManifestAddress then
            None
        else
            let payloads = payloadByAddress registration.BlockPayloads
            let blocks = ResizeArray<RegisteredContentBlock>()

            if not (isNull registration.Manifest.Blocks) then
                for block in registration.Manifest.Blocks do
                    if not (isNull (box block)) then
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

    let rebuild sources =
        if isNull sources then
            Array.empty
        else
            sources |> Array.collect recordsAfterFinalize

    let private isSameCandidateWindow (left: DedupeIndexRecord) (right: DedupeIndexRecord) =
        left.StoragePoolId = right.StoragePoolId
        && left.ManifestAddress = right.ManifestAddress
        && left.ContentBlockAddress = right.ContentBlockAddress
        && left.OrdinalStart = right.OrdinalStart
        && left.OrdinalCount = right.OrdinalCount

    let private writeRecords (newRecords: DedupeIndexRecord array) =
        for record in newRecords do
            let hasNewerRecord =
                records.Values
                |> Seq.exists (fun existing ->
                    isSameCandidateWindow existing record
                    && existing.MetadataVersion > record.MetadataVersion)

            if not hasNewerRecord then
                for existing in records.Values do
                    if isSameCandidateWindow existing record
                       && existing.MetadataVersion <= record.MetadataVersion then
                        records.TryRemove(recordKey existing) |> ignore

                records[recordKey record] <- record

    let private removeRecordsForMetadataBlock storagePoolId contentBlockAddress metadataVersion =
        for existing in records.Values do
            if existing.StoragePoolId = storagePoolId
               && existing.ContentBlockAddress = contentBlockAddress
               && existing.MetadataVersion <= metadataVersion then
                records.TryRemove(recordKey existing) |> ignore

    let replaceAll (newRecords: DedupeIndexRecord array) =
        records.Clear()
        writeRecords newRecords

    let writeAfterFinalize (source: FinalizedManifestIndexSource) =
        let newRecords = recordsAfterFinalize source
        writeRecords newRecords

        newRecords

    let private runtimeRegistrationMatchesMetadata (metadata: ContentBlockMetadata) (registration: RuntimeFinalizedManifestRegistration) =
        registration.StoragePoolId = metadata.StoragePoolId
        && (registration.Blocks
            |> Array.exists (fun block -> block.Address = metadata.ContentBlockAddress))

    let private writeForRegistrationWithMetadata (registration: RuntimeFinalizedManifestRegistration) (metadata: ContentBlockMetadata) =
        let output = ResizeArray<DedupeIndexRecord>()

        removeRecordsForMetadataBlock registration.StoragePoolId metadata.ContentBlockAddress metadata.MetadataVersion

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
        writeRecords newRecords
        newRecords

    let writeAfterAuthoritativeMetadata (metadata: ContentBlockMetadata) =
        if
            isNull (box metadata)
            || String.IsNullOrWhiteSpace metadata.ContentBlockAddress
        then
            Array.empty
        else
            metadataRecords[metadataKey metadata] <- metadata
            removeRecordsForMetadataBlock metadata.StoragePoolId metadata.ContentBlockAddress metadata.MetadataVersion

            let matchingRegistrations =
                finalizedManifests
                |> Seq.filter (fun kvp -> runtimeRegistrationMatchesMetadata metadata kvp.Value)
                |> Seq.toArray

            let newRecords =
                matchingRegistrations
                |> Seq.collect (fun kvp -> writeForRegistrationWithMetadata kvp.Value metadata)
                |> Seq.toArray

            newRecords

    let registerFinalizedManifest (registration: FinalizedManifestRegistration) =
        match tryCreateRuntimeRegistration registration with
        | None -> Array.empty
        | Some runtimeRegistration ->
            let key = finalizedManifestKey registration
            finalizedManifests[key] <- runtimeRegistration

            let newRecords =
                metadataRecords.Values
                |> Seq.filter (fun metadata -> runtimeRegistrationMatchesMetadata metadata runtimeRegistration)
                |> Seq.collect (writeForRegistrationWithMetadata runtimeRegistration)
                |> Seq.toArray

            newRecords

    let snapshot () = records.Values |> Seq.toArray

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
