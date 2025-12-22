namespace Grace.Server.Services

open Grace.Shared.Utilities
open Grace.Types.Types
open System

module ConflictResolution =

    type ConflictSuggestion =
        { RelativePath: string
          SuggestedContent: string
          Confidence: int }

    let suggestConflictResolution (relativePath: string) =
        { RelativePath = relativePath
          SuggestedContent = String.Empty
          Confidence = 0 }
