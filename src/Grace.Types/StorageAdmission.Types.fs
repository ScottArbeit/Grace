namespace Grace.Types

open Grace.Types.Common
open Grace.Types.Repository
open System
open System.Collections.Generic

/// Owns pure storage-admission limits and untrusted event-property validation.
module StorageAdmission =

    [<Literal>]
    let StandardMaximumFileBytes = 1_048_576L

    [<Literal>]
    let DefaultLargeFileMaximumBytes = 104_857_600L

    [<Literal>]
    let UploadSessionIdsProperty = "UploadSessionIds"

    /// Returns the finite server maximum used when a repository enables large files.
    let largeFileMaximumBytes () =
        match Int64.TryParse(Environment.GetEnvironmentVariable "grace__storage__maximum_large_file_bytes") with
        | true, configured when configured > StandardMaximumFileBytes -> configured
        | _ -> DefaultLargeFileMaximumBytes

    /// Rejects a logical file size outside the repository's configured admission tier.
    let validateFileSize repositoryDto expectedSize correlationId =
        let maximum =
            if repositoryDto.AllowsLargeFiles then
                largeFileMaximumBytes ()
            else
                StandardMaximumFileBytes

        if expectedSize <= 0L then
            Error(GraceError.Create "Manifest upload ExpectedSize must be greater than zero." correlationId)
        elif expectedSize > maximum then
            Error(GraceError.Create $"Manifest upload size {expectedSize} exceeds the repository limit of {maximum} bytes." correlationId)
        else
            Ok()

    /// Validates the only client-settable event property and returns canonical metadata text.
    let canonicalizeClientProperties correlationId (properties: Dictionary<string, string>) =
        let supplied =
            if isNull properties then
                Dictionary<string, string>(StringComparer.Ordinal)
            else
                properties

        if supplied.Count = 0 then
            Ok(Dictionary<string, string>(StringComparer.Ordinal))
        elif
            supplied.Count <> 1
            || not (supplied.ContainsKey UploadSessionIdsProperty)
        then
            Error(GraceError.Create "Client event properties contain a protected or unsupported key." correlationId)
        else
            let value = supplied[UploadSessionIdsProperty]

            if String.IsNullOrEmpty value then
                Error(GraceError.Create "UploadSessionIds must be omitted when no manifest upload was performed." correlationId)
            else
                let segments = value.Split(',', StringSplitOptions.None)
                let parsed = ResizeArray<Guid>()
                let mutable malformed = false

                for segment in segments do
                    let mutable uploadSessionId = Guid.Empty

                    if
                        not (Guid.TryParse(segment, &uploadSessionId))
                        || uploadSessionId = Guid.Empty
                    then
                        malformed <- true
                    else
                        parsed.Add uploadSessionId

                let canonicalIds =
                    parsed
                    |> Seq.map (fun uploadSessionId -> uploadSessionId.ToString("N"))
                    |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                    |> Seq.toArray

                let hasDuplicates =
                    canonicalIds |> Array.distinct |> Array.length
                    <> canonicalIds.Length

                let canonicalValue = String.Join(',', canonicalIds)

                if malformed then
                    Error(GraceError.Create "UploadSessionIds contains a malformed upload session id." correlationId)
                elif hasDuplicates then
                    Error(GraceError.Create "UploadSessionIds contains a duplicate upload session id." correlationId)
                elif not (String.Equals(value, canonicalValue, StringComparison.Ordinal)) then
                    Error(GraceError.Create "UploadSessionIds must use canonical lowercase sorted GUID values without whitespace." correlationId)
                else
                    let canonical = Dictionary<string, string>(StringComparer.Ordinal)
                    canonical.Add(UploadSessionIdsProperty, canonicalValue)
                    Ok canonical
