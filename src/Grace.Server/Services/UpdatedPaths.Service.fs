namespace Grace.Server.Services

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.PatchSet
open Grace.Types.Types
open System
open System.Collections.Generic
open System.Linq

module UpdatedPaths =

    let private normalizeSegments (relativePath: RelativePath) =
        let normalized = normalizeFilePath $"{relativePath}"
        normalized.Split("/", StringSplitOptions.RemoveEmptyEntries)

    let private buildKey (segments: string array) =
        let l1 = if segments.Length > 0 then segments[0] else "_root_"
        let l2 = if segments.Length > 1 then segments[1] else "_none_"
        let l3 = if segments.Length > 2 then segments[2] else "_none_"
        { L1 = l1; L2 = l2; L3 = l3 }

    let compute (paths: string seq) =
        let keys = Dictionary<string, PathKey>(StringComparer.OrdinalIgnoreCase)

        for path in paths do
            let segments = normalizeSegments path
            let key = buildKey segments
            let comparisonKey = $"{key.L1}/{key.L2}/{key.L3}"

            if not (keys.ContainsKey(comparisonKey)) then
                keys[comparisonKey] <- key

        { UpdatedPaths.Default with Keys = keys.Values.ToArray() }
