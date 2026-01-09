namespace Grace.Shared

open DiffPlex.DiffBuilder.Model
open Grace.Shared.Utilities
open Grace.Types.Diff
open Grace.Types.Review
open Grace.Types.Types
open System
open System.Collections.Generic
open System.Linq
open System.Text
open System.Text.RegularExpressions

module Evidence =
    let private estimateTokens (byteCount: int) = if byteCount <= 0 then 0 else max 1 (byteCount / 4)

    let private redact (patterns: string list) (content: string) =
        let redacted =
            (content, patterns)
            ||> List.fold (fun current pattern ->
                if String.IsNullOrWhiteSpace pattern then
                    current
                else
                    Regex.Replace(current, pattern, "***REDACTED***"))

        redacted, not (String.Equals(content, redacted, StringComparison.Ordinal))

    let private getLineBounds (lines: DiffPiece array) =
        let positions =
            lines
            |> Seq.choose (fun piece -> if piece.Position.HasValue then Some piece.Position.Value else None)
            |> Seq.toList

        match positions with
        | [] ->
            let lineCount = max 1 lines.Length
            1, lineCount
        | _ -> positions |> List.min, positions |> List.max

    let private scoreReasons (riskProfile: DeterministicRiskProfile option) (relativePath: RelativePath) (lines: DiffPiece array) =
        let changedLineCount =
            lines
            |> Array.filter (fun piece -> piece.Type <> ChangeType.Unchanged)
            |> Array.length

        let mutable reasons = List<EvidenceScoreReason>()

        if changedLineCount > 0 then
            reasons.Add({ Feature = "ChangeMagnitude"; Score = float changedLineCount })

        match riskProfile with
        | Some profile ->
            if profile.SensitivePathsTouched |> List.contains relativePath then
                reasons.Add({ Feature = "SensitivePath"; Score = 10.0 })

            if profile.DependencyConfigChanges |> List.contains relativePath then
                reasons.Add({ Feature = "DependencyConfig"; Score = 5.0 })

            if profile.ApiSurfaceSignals |> List.contains relativePath then
                reasons.Add({ Feature = "ApiSurfaceSignal"; Score = 5.0 })
        | None -> ()

        let score = reasons |> Seq.sumBy (fun reason -> reason.Score)
        score, reasons |> Seq.toList

    let private topReasons (sliceSummaries: EvidenceSliceSummary list) =
        sliceSummaries
        |> Seq.collect (fun summary -> summary.Reasons)
        |> Seq.groupBy (fun reason -> reason.Feature)
        |> Seq.map (fun (feature, reasons) -> { Feature = feature; Score = reasons |> Seq.sumBy (fun reason -> reason.Score) })
        |> Seq.sortByDescending (fun reason -> reason.Score)
        |> Seq.truncate 3
        |> Seq.toList

    let buildEvidenceSet
        (stage: EvidenceStage)
        (budget: EvidenceBudget)
        (riskProfile: DeterministicRiskProfile option)
        (redactionPatterns: string list)
        (diff: DiffDto)
        =
        let maxFiles = max 0 budget.MaxFiles
        let maxHunks = max 0 budget.MaxHunksPerFile
        let maxLines = max 0 budget.MaxLinesPerHunk
        let maxTotalBytes = if budget.MaxTotalBytes <= 0 then Int32.MaxValue else budget.MaxTotalBytes

        let slices = List<EvidenceSlice>()
        let sliceSummaries = List<EvidenceSliceSummary>()
        let mutable totalBytes = 0

        let fileDiffs =
            diff.FileDiffs
            |> Seq.cast<FileDiff>
            |> Seq.sortBy (fun fileDiff -> fileDiff.RelativePath)
            |> Seq.truncate maxFiles
            |> Seq.toList

        for fileDiff in fileDiffs do
            if not fileDiff.IsBinary then
                let sections =
                    fileDiff.InlineDiff
                    |> Seq.cast<DiffPiece array>
                    |> Seq.truncate maxHunks
                    |> Seq.toList

                for section in sections do
                    let limitedLines = if maxLines = 0 then section else section |> Array.truncate maxLines

                    let content =
                        limitedLines
                        |> Array.map (fun piece -> piece.Text)
                        |> String.concat Environment.NewLine

                    let redactedContent, isRedacted = redact redactionPatterns content
                    let bytes = Encoding.UTF8.GetByteCount(redactedContent)

                    if totalBytes + bytes <= maxTotalBytes then
                        let startLine, endLine = getLineBounds limitedLines
                        let score, reasons = scoreReasons riskProfile fileDiff.RelativePath limitedLines

                        slices.Add(
                            { RelativePath = fileDiff.RelativePath
                              StartLine = startLine
                              EndLine = endLine
                              Content = redactedContent
                              IsRedacted = isRedacted }
                        )

                        sliceSummaries.Add({ RelativePath = fileDiff.RelativePath; StartLine = startLine; EndLine = endLine; Score = score; Reasons = reasons })

                        totalBytes <- totalBytes + bytes

        let estimatedTokens = estimateTokens totalBytes

        let evidenceSet = { Stage = stage; Slices = slices |> Seq.toList; Budget = budget; TotalBytes = totalBytes; EstimatedTokens = estimatedTokens }

        let evidenceSummary =
            { Stage = stage
              SelectedFiles = fileDiffs |> List.map (fun fileDiff -> fileDiff.RelativePath)
              SliceSummaries = sliceSummaries |> Seq.toList
              Budget = budget
              TotalBytes = totalBytes
              EstimatedTokens = estimatedTokens
              TopReasons = topReasons (sliceSummaries |> Seq.toList) }

        evidenceSet, evidenceSummary
