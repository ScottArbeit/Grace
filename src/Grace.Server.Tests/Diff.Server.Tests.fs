namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Common
open NUnit.Framework
open System
open System.Net

module DiffServerTestHelpers =
    let getDiffParameters (repositoryId: string) (directoryVersionId1: DirectoryVersionId) (directoryVersionId2: DirectoryVersionId) =
        let parameters = Parameters.Diff.GetDiffParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.DirectoryVersionId1 <- directoryVersionId1
        parameters.DirectoryVersionId2 <- directoryVersionId2
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let getDiffBySha256HashParameters (repositoryId: string) (sha256Hash1: Sha256Hash) (sha256Hash2: Sha256Hash) =
        let parameters = Parameters.Diff.GetDiffBySha256HashParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.Sha256Hash1 <- sha256Hash1
        parameters.Sha256Hash2 <- sha256Hash2
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

[<NonParallelizable>]
type DiffServer() =

    [<Test>]
    member _.DiffRoutesRejectInvalidDirectoryIdsAndShaInputsBeforeReturningSuccessEnvelopes() =
        task {
            let repositoryId = repositoryIds[2]

            let invalidDirectoryParameters = DiffServerTestHelpers.getDiffParameters repositoryId Guid.Empty (Guid.NewGuid())

            let! invalidDirectoryResponse = Client.PostAsync("/diff/getDiff", createJsonContent invalidDirectoryParameters)
            let! invalidDirectoryBody = invalidDirectoryResponse.Content.ReadAsStringAsync()
            Assert.That(invalidDirectoryResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), invalidDirectoryBody)
            let invalidDirectoryError = deserialize<GraceError> invalidDirectoryBody
            Assert.That(invalidDirectoryError.Error, Is.EqualTo(DiffError.getErrorMessage DiffError.InvalidDirectoryVersionId))
            Assert.That(invalidDirectoryError.CorrelationId, Is.Not.Empty)

            let invalidHashParameters = DiffServerTestHelpers.getDiffBySha256HashParameters repositoryId "not-a-sha" "also-not-a-sha"

            let! invalidHashResponse = Client.PostAsync("/diff/getDiffBySha256Hash", createJsonContent invalidHashParameters)
            let! invalidHashBody = invalidHashResponse.Content.ReadAsStringAsync()
            Assert.That(invalidHashResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), invalidHashBody)
            let invalidHashError = deserialize<GraceError> invalidHashBody
            Assert.That(invalidHashError.Error, Is.EqualTo(DiffError.getErrorMessage DiffError.InvalidSha256Hash))
            Assert.That(invalidHashError.CorrelationId, Is.Not.Empty)
        }
