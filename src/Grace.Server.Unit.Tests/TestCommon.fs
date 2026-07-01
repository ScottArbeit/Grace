namespace Grace.Server.Tests

open Grace.Shared.Validation.Errors

/// Groups server unit test helpers for common.
module Common =
    let okResult: Result<unit, TestError> = Result.Ok()
    let errorResult: Result<unit, TestError> = Result.Error TestError.TestFailed
