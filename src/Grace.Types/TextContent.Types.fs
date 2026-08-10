namespace Grace.Types

open Grace.Types.Common
open System

/// Defines immutable repository-scoped text storage facts and description references.
module TextContent =
    /// Identifies immutable stored text without exposing its object location or storage format.
    type TextContent = { TextContentId: TextContentId; Blake3Hash: Blake3Hash; Utf8ByteLength: int64 }

    /// Represents one immutable work-item description event, with no text when a later clear operation is recorded.
    type Description = { DescriptionId: DescriptionId; TextContent: TextContent option }
