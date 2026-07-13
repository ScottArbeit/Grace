namespace Grace.Types

/// Identifies how Grace may satisfy a Materialization Plan request.
type MaterializationExecutionMode =
    | Direct = 1
    | CachePreferred = 2
    | CacheRequired = 3
