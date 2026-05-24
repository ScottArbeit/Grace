namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NUnit.Framework
open System
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<NonParallelizable>]
type StorageWholeFileCompatibility() =

    let computeSha256Hash (bytes: byte array) =
        let hash = SHA256.HashData(bytes)
        byteArrayToString (hash.AsSpan())

    let tryGetJsonProperty (name: string) (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.tryFind (fun property -> property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun property -> property.Value)

    let requireJsonProperty (name: string) (element: JsonElement) =
        match tryGetJsonProperty name element with
        | Some value -> value
        | None ->
            Assert.Fail($"Expected JSON property '{name}'.")
            Unchecked.defaultof<JsonElement>

    let assertWholeFileContentReference (metadata: JsonElement) =
        let contentReference = requireJsonProperty "ContentReference" metadata
        let referenceType = requireJsonProperty "ReferenceType" contentReference

        match referenceType.ValueKind with
        | JsonValueKind.Number -> Assert.That(referenceType.GetInt32(), Is.EqualTo(int FileContentReferenceType.WholeFileContent))
        | JsonValueKind.String -> Assert.That(referenceType.GetString(), Is.EqualTo("WholeFileContent").IgnoreCase)
        | _ -> Assert.Fail($"Unexpected ContentReference.ReferenceType JSON kind: {referenceType.ValueKind}.")

        match tryGetJsonProperty "Manifest" contentReference with
        | Some manifest -> Assert.That(manifest.ValueKind, Is.EqualTo(JsonValueKind.Null))
        | None -> ()

    let getUploadMetadataJson (content: string) =
        use document = JsonDocument.Parse(content)
        let root = document.RootElement.Clone()
        let returnValue = requireJsonProperty "ReturnValue" root
        returnValue.EnumerateArray() |> Seq.exactlyOne

    let createUploadParameters repositoryId fileVersion =
        let parameters = Parameters.Storage.GetUploadMetadataForFilesParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.FileVersions <- [| fileVersion |]
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createDownloadParameters repositoryId fileVersion =
        let parameters = Parameters.Storage.GetDownloadUriParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.FileVersion <- fileVersion
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    [<Test>]
    member _.SmallWholeFileContentPopulatesContentReferenceWithoutChangingEndpointMetadata() =
        task {
            let repositoryId = repositoryIds[0]
            let relativeDirectory = $"wholefile-compatibility/{Guid.NewGuid():N}"
            let relativePath = $"{relativeDirectory}/small.bin"
            let payload = Encoding.UTF8.GetBytes($"Grace whole-file compatibility {Guid.NewGuid():N}")
            let sha256Hash = computeSha256Hash payload
            let fileVersion = FileVersion.Create relativePath sha256Hash String.Empty true (int64 payload.Length)

            let! uploadResponse = Client.PostAsync("/storage/getUploadMetadataForFiles", createJsonContent (createUploadParameters repositoryId fileVersion))
            let! uploadContent = uploadResponse.Content.ReadAsStringAsync()
            Assert.That(uploadResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadContent)

            let metadata = getUploadMetadataJson uploadContent

            Assert.That(
                (requireJsonProperty "RelativePath" metadata)
                    .GetString(),
                Is.EqualTo(relativePath)
            )

            Assert.That(
                (requireJsonProperty "Sha256Hash" metadata)
                    .GetString(),
                Is.EqualTo(sha256Hash)
            )

            assertWholeFileContentReference metadata

            let blobUri =
                Uri(
                    (requireJsonProperty "BlobUriWithSasToken" metadata)
                        .GetString()
                )

            Assert.That(blobUri.IsAbsoluteUri, Is.True)

            let! downloadResponse = Client.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId fileVersion))
            let! downloadUri = downloadResponse.Content.ReadAsStringAsync()
            Assert.That(downloadResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), downloadUri)
            Assert.That(downloadUri, Does.Contain(fileVersion.GetObjectFileName))
        }
