# Computing the SHA-256 value for files and directories

## Introduction

Grace uses the [SHA-256](https://en.wikipedia.org/wiki/SHA-2) algorithm to compute cryptographically verifiable hash values for files and directories stored in Grace. The use of SHA-256 hashes for this purpose is inspired by Git's use of SHA-1, and their ongoing work to migrate to SHA-256.

The SHA-256 hash values are used throughout Grace to identify unique versions of files and directories, primarily to minimize the size of change captured by each reference (i.e. each save, checkpoint, commit, and tag).

Hash values in Grace are meant to provide cryptographic proof that the directory and file versions stored for each reference match what was originally uploaded. To be more precise, when a user downloads a specific version of a branch, the files and directory versions should provably be the same versions that were originally stored in Grace. Grace Server, as part of maintenance routines, should also be able to verify file and directory versions at any time.

Unlike Git, the SHA-256 values provide no linkage between references. Each SHA-256 value is specific to that version of the repository, with no connection to previous or subsequent versions. This enables Grace to be able to delete references and versions, such as saves that are no longer necessary, without the manipulation of history required in Git.

The choice of SHA-256, as opposed to SHA-384 or other stronger algorithms, comes from a desire to provide excellent runtime performance with strong cryptographic hashing. SHA-256 has been studied extensively [for over 20 years](https://en.wikipedia.org/wiki/SHA-2), and [seems to be collision-resistant to quantum algorithms](https://crypto.stackexchange.com/questions/59375/are-hash-functions-strong-against-quantum-cryptanalysis-and-or-independent-enoug). Because Grace, like Git, uses it here solely for hashing, and not for encryption, any potential long-term issues with SHA-256 leave only a small opportunity for misuse.

> This information is valid as of April, 2022. The implementation may change as Grace matures.

## Implementation

In ordinary usage, SHA-256 values are computed by Grace CLI, and those values are used when uploading versions of files and directories, and when creating references on the server.

Specifically, Grace relies on the .NET implementation of [SHA-256](https://docs.microsoft.com/en-us/dotnet/api/system.security.cryptography.sha256?view=net-6.0), found in the [System.Security.Cryptography](https://docs.microsoft.com/en-us/dotnet/api/system.security.cryptography?view=net-6.0) namespace.

.NET implementations of Grace clients are welcome to use the hashing implementation in Grace.Shared, which is used by both Grace CLI and Grace Server. Implementations in other languages will need to implement this algorithm separately.

Grace Server rechecks each SHA-256 hash as versions arrive at the server. In the event of a discrepancy, which could indicate malicious behavior, the invalid versions will be deleted, and Grace administrators and repository owners will be notified.

### Files
When computing the SHA-256 value for a file, Grace uses a stream - FileStream when reading a local file, and Stream when reading a file from object storage - and the [IncrementalHash](https://docs.microsoft.com/en-us/dotnet/api/system.security.cryptography.incrementalhash) class, to keep memory use constant, no matter the size of the file.

The SHA-256 value for a file is computed with the following algorithm.

1. An instance of the [IncrementalHash](https://docs.microsoft.com/en-us/dotnet/api/system.security.cryptography.incrementalhash) class is created using the SHA-256 hash algorithm.
2. Bytes from the file are read from the stream into a 64KB buffer, and appended to the IncrementalHash's input data, until the stream is consumed.
3. The relative path of the file (based on the repository root), represented as a string, is converted to bytes using UTF-8 encoding, and appended to the IncrementalHash's input data.

    > For consistency between Windows and Unix-y OS's, Grace converts backslashes `\` in the relative path into forward slashes `/` before converting to bytes.

    - For example: a file is being uploaded on Windows with full path name `C:\Source\MyRepo\SomeDir\myfile.md`. The root of the repository is in `C:\Source\MyRepo`. Therefore, the relative path of the file is `.\SomeDir\myfile.md`. This string will be converted to `./SomeDir/myfile.md` before being converted to a byte array and appended to the IncrementalHash's input.
4. The length of the file, represented as an `Int64` value, is converted to bytes and appended to the IncrementalHash's input data.
5. The SHA-256 hash is computed as a `byte[]`.
6. The SHA-256 hash is converted to a string by converting each byte to a two-character hexadecimal value.
    - For example, `byte[]{0x43, 0x2a, 0x01, 0xfa}` would be converted to a string `"432a01fa"`.

The code for this can be found in the Grace.Shared project, in Utilities.Shared.fs.

``` fsharp
let computeSha256ForFile (stream: Stream) (relativeFilePath: String) =
    task {
        let bufferLength = 64 * 1024
        let buffer = ArrayPool<byte>.Shared.Rent(bufferLength)

        try
            // 1. Create an IncrementalHash instance.
            use hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            // 2. Read bytes from the stream and feed them into the hasher.
            let mutable loop = true
            while loop do
                let! bytesRead = stream.ReadAsync(buffer.AsMemory(0, bufferLength))
                if bytesRead > 0 then
                    hasher.AppendData(buffer.AsSpan(0, bytesRead))
                else
                    loop <- false
            // 3. Convert the relative path of the file to a byte array, and add it to the hasher.
            hasher.AppendData(Encoding.UTF8.GetBytes(relativeFilePath))
            // 4. Convert the Int64 file length into a byte array, and add it to the hasher.
            hasher.AppendData(BitConverter.GetBytes(stream.Length))
            // 5. Get the SHA-256 hash as a `byte[]`.
            let sha256Bytes = hasher.GetHashAndReset()
            // 6. Convert the SHA-256 value from a byte[] to a string, and return it.
            //    Example: byte[]{0x43, 0x2a, 0x01, 0xfa} -> "432a01fa"
            return byteArrayAsString(sha256Bytes)
        finally
            ArrayPool<byte>.Shared.Return(buffer, clearArray = true)
    }
```

### Directories
The SHA-256 hash of a directory is computed using the following algorithm.

1. The relative path of the directory (based on the repository root), represented as a string, is converted to bytes and stored in a `List<byte>`.
    - For example: a directory version is being uploaded with full path name `C:\Source\MyRepo\SomeDir\SomeSubDir`. The root of the repository is in `C:\Source\MyRepo`. Therefore, the relative path of the directory is `.\SomeDir\SomeSubDir`. This string will be converted to `./SomeDir/SomeSubDir` before being converted to a byte array and used to create the `List<byte>`.
2. The list of _subdirectories_ is sorted by name, using [CultureInfo.InvariantCulture](https://docs.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo.invariantculture). The SHA-256 value for each subdirectory is converted to a `byte[]` using UTF-8 encoding, and appended, in order, to the `List<byte>`. The sorted list of subdirectories does not include the `/.` and `/..` directories.
3. The list of _files_ in the directory is sorted by name, using [CultureInfo.InvariantCulture](https://docs.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo.invariantculture). The SHA-256 value for each file is converted to a `byte[]` using UTF-8 encoding, and appended, in order, to the `List<byte>`.
4. The entire `List<byte>` is used as input to compute the SHA-256 value. The SHA-256 value is represented as a `byte[]`.
5. The SHA-256 hash is converted to a string by converting each byte to a two-character hexadecimal value.
    - For example, `byte[]{0x43, 0x2a, 0x01, 0xfa}` would be converted to a string `"432a01fa"`.

### Validation
Grace will provide a command - `grace branch verify` - that will compute the SHA-256 values for on-disk versions of files and directories, and compare them to the SHA-256 values stored in Grace's database.