namespace Grace.Server.Tests

open Grace.Shared.Validation.Errors

module Common =
    let okResult: Result<unit, TestError> = Result.Ok()
    let errorResult: Result<unit, TestError> = Result.Error TestError.TestFailed
