namespace Grace.Shared

open Grace.Types.Common
open System
open System.IO
open System.IO.Compression

/// Contains manifest eligibility helpers.
module ManifestEligibility =
    /// Normalizes normalized scan bytes.
    let private normalizedScanBytes (policy: ManifestEligibilityPolicy) contentLength = Math.Min(Math.Max(policy.BinaryScanBytes, 0), contentLength)

    /// Detects binary content by scanning only the configured prefix for a NUL byte.
    let detectBinaryContent (policy: ManifestEligibilityPolicy) (content: byte array) =
        ArgumentNullException.ThrowIfNull(content)

        let bytesToScan = normalizedScanBytes policy content.Length
        let mutable isBinary = false
        let mutable index = 0

        while index < bytesToScan && not isBinary do
            if content[index] = 0uy then isBinary <- true

            index <- index + 1

        isBinary

    /// Returns the byte count produced by Grace's gzip settings for text content.
    let getGraceGzipCompressedSize (content: byte array) =
        ArgumentNullException.ThrowIfNull(content)

        use compressed = new MemoryStream()

        do
            use gzipStream = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen = true)

            gzipStream.Write(content, 0, content.Length)

        compressed.Length

    /// Evaluates whether content should remain whole-file content or be stored through a file manifest.
    let evaluateContentReferenceType (policy: ManifestEligibilityPolicy) (content: byte array) =
        ArgumentNullException.ThrowIfNull(content)

        let measuredSize =
            if detectBinaryContent policy content then
                int64 content.Length
            else
                getGraceGzipCompressedSize content

        if measuredSize >= policy.ThresholdBytes then
            FileContentReferenceType.FileManifest
        else
            FileContentReferenceType.WholeFileContent
