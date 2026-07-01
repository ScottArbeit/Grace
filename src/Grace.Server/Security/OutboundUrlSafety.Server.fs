namespace Grace.Server.Security

open Grace.Shared.Utilities
open Grace.Types.Webhooks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open System
open System.Collections.Generic
open System.Globalization
open System.Net
open System.Security.Cryptography
open System.Text

/// Contains Grace Server outbound url safety behavior and supporting helpers.
module OutboundUrlSafety =

    [<Literal>]
    let UnsafeLocalDevelopmentConfigKey = "grace__webhooks__outbound_urls__allow_unsafe_local_development"

    /// Represents redirect policy used by Grace Server APIs and background services.
    type RedirectPolicy =
        | RefuseRedirects
        | RevalidateEveryRedirect

    /// Represents validation failure used by Grace Server APIs and background services.
    type ValidationFailure =
        | MissingUrl
        | InvalidUri
        | UnsupportedScheme of string
        | HttpsRequired
        | EmbeddedCredentialsRejected
        | FragmentRejected
        | HostRequired
        | LocalTargetRequiresDevelopmentAcknowledgement
        | UnsafeHostRejected of string

    /// Represents validation request used by Grace Server APIs and background services.
    type ValidationRequest =
        {
            Url: string
            RequestedSafety: OutboundUrlSafety
            AcknowledgeUnsafeLocalDevelopment: bool
        }

        static member PublicHttps url = { Url = url; RequestedSafety = OutboundUrlSafety.PublicHttps; AcknowledgeUnsafeLocalDevelopment = false }

    /// Represents validated outbound url used by Grace Server APIs and background services.
    type ValidatedOutboundUrl = { Uri: Uri; ScopedUrl: ScopedOutboundUrl; RedirectPolicy: RedirectPolicy; ResolvedAddresses: IPAddress array }

    /// Represents signing input used by Grace Server APIs and background services.
    type SigningInput = { RequestId: string; Timestamp: string; KeyId: string; PayloadSha256Hex: string; SignedMaterial: byte array }

    /// Represents host address resolver used by Grace Server APIs and background services.
    type HostAddressResolver = string -> IPAddress array

    /// Gets try get config value data needed by the server flow.
    let private tryGetConfigValue (configuration: IConfiguration) (name: string) =
        if isNull configuration then
            None
        else
            let value = configuration[getConfigKey name]
            if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    /// Determines whether unsafe local development enabled.
    let isUnsafeLocalDevelopmentEnabled (configuration: IConfiguration) =
        match tryGetConfigValue configuration UnsafeLocalDevelopmentConfigKey with
        | Some value ->
            String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || String.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || String.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
        | None -> false

    /// Determines whether development host environment.
    let isDevelopmentHostEnvironment (hostEnvironment: IHostEnvironment) = if isNull hostEnvironment then false else hostEnvironment.IsDevelopment()

    /// Determines whether localhost name.
    let private isLocalhostName (host: string) = String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)

    /// Parses try parse ipaddress input into the server model.
    let private tryParseIPAddress (host: string) =
        let mutable address = Unchecked.defaultof<IPAddress>

        if IPAddress.TryParse(host, &address) then Some address else None

    /// Determines whether ipv4 in range.
    let private isIPv4InRange (address: IPAddress) (prefix: byte array) (prefixLength: int) =
        let bytes = address.GetAddressBytes()

        if bytes.Length <> 4 then
            false
        else
            let mutable remainingBits = prefixLength
            let mutable matches = true
            let mutable index = 0

            while matches && remainingBits > 0 do
                let bitsInByte = Math.Min(remainingBits, 8)
                let mask = byte (0xFF <<< (8 - bitsInByte))
                matches <- (bytes[index] &&& mask) = (prefix[index] &&& mask)
                remainingBits <- remainingBits - bitsInByte
                index <- index + 1

            matches

    /// Determines whether ipv6 in range.
    let private isIPv6InRange (address: IPAddress) (prefix: byte array) (prefixLength: int) =
        let bytes = address.GetAddressBytes()

        if bytes.Length <> 16 then
            false
        else
            let mutable remainingBits = prefixLength
            let mutable matches = true
            let mutable index = 0

            while matches && remainingBits > 0 do
                let bitsInByte = Math.Min(remainingBits, 8)
                let mask = byte (0xFF <<< (8 - bitsInByte))
                matches <- (bytes[index] &&& mask) = (prefix[index] &&& mask)
                remainingBits <- remainingBits - bitsInByte
                index <- index + 1

            matches

    /// Determines whether unsafe ipv4 address.
    let private isUnsafeIPv4Address (address: IPAddress) =
        isIPv4InRange address [| 0uy; 0uy; 0uy; 0uy |] 8
        || isIPv4InRange address [| 10uy; 0uy; 0uy; 0uy |] 8
        || isIPv4InRange address [| 100uy; 64uy; 0uy; 0uy |] 10
        || isIPv4InRange address [| 127uy; 0uy; 0uy; 0uy |] 8
        || isIPv4InRange address [| 169uy; 254uy; 0uy; 0uy |] 16
        || isIPv4InRange address [| 172uy; 16uy; 0uy; 0uy |] 12
        || isIPv4InRange address [| 192uy; 0uy; 0uy; 0uy |] 24
        || isIPv4InRange address [| 192uy; 0uy; 2uy; 0uy |] 24
        || isIPv4InRange address [| 192uy; 88uy; 99uy; 0uy |] 24
        || isIPv4InRange address [| 192uy; 168uy; 0uy; 0uy |] 16
        || isIPv4InRange address [| 198uy; 18uy; 0uy; 0uy |] 15
        || isIPv4InRange address [| 198uy; 51uy; 100uy; 0uy |] 24
        || isIPv4InRange address [| 203uy; 0uy; 113uy; 0uy |] 24
        || isIPv4InRange address [| 224uy; 0uy; 0uy; 0uy |] 4
        || isIPv4InRange address [| 240uy; 0uy; 0uy; 0uy |] 4

    let private unsafeIPv6SpecialPurposeRanges =
        [|
            "::", 128, "Unspecified address"
            "::1", 128, "Loopback address"
            "::ffff:0:0", 96, "IPv4-mapped address"
            "64:ff9b::", 96, "Well-known NAT64 prefix"
            "64:ff9b:1::", 48, "Local-use NAT64 prefix"
            "100::", 64, "Discard-only prefix"
            "100:0:0:1::", 64, "Dummy IPv6 prefix"
            "2001::", 32, "Teredo"
            "2001:2::", 48, "Benchmarking"
            "2001:10::", 28, "Deprecated ORCHID"
            "2001:db8::", 32, "Documentation"
            "2002::", 16, "6to4"
            "3fff::", 20, "Documentation"
            "5f00::", 16, "Segment Routing SIDs"
            "fc00::", 7, "Unique local"
            "fec0::", 10, "Deprecated site-local"
            "fe80::", 10, "Link-local unicast"
            "ff00::", 8, "Multicast"
        |]
        |> Array.map (fun (prefix, prefixLength, _) -> IPAddress.Parse(prefix).GetAddressBytes(), prefixLength)

    /// Determines whether unsafe ipv6 address.
    let private isUnsafeIPv6Address (address: IPAddress) =
        unsafeIPv6SpecialPurposeRanges
        |> Array.exists (fun (prefix, prefixLength) -> isIPv6InRange address prefix prefixLength)

    /// Normalizes normalize address data for stable server comparisons.
    let private normalizeAddress (address: IPAddress) = if address.IsIPv4MappedToIPv6 then address.MapToIPv4() else address

    /// Determines whether unsafe address.
    let private isUnsafeAddress (address: IPAddress) =
        let normalizedAddress = normalizeAddress address

        match normalizedAddress.AddressFamily with
        | Net.Sockets.AddressFamily.InterNetwork when
            address.IsIPv4MappedToIPv6
            && isUnsafeIPv6Address address
            ->
            true
        | Net.Sockets.AddressFamily.InterNetwork -> isUnsafeIPv4Address normalizedAddress
        | Net.Sockets.AddressFamily.InterNetworkV6 -> isUnsafeIPv6Address normalizedAddress
        | _ -> true

    /// Coordinates address for failure processing for Grace Server.
    let private addressForFailure (address: IPAddress) = (normalizeAddress address).ToString()

    let private resolveHostAddresses: HostAddressResolver = fun host -> Dns.GetHostAddresses(host)

    /// Normalizes normalize addresses data for stable server comparisons.
    let private normalizeAddresses (addresses: IPAddress array) =
        addresses
        |> Array.map normalizeAddress
        |> Array.distinctBy (fun address -> address.ToString())

    /// Validates validate resolved addresses inputs before server processing continues.
    let private validateResolvedAddresses host (addresses: IPAddress array) =
        if isNull addresses || addresses.Length = 0 then
            Error(UnsafeHostRejected host)
        else
            match addresses |> Array.tryFind isUnsafeAddress with
            | Some address -> Error(UnsafeHostRejected(addressForFailure address))
            | None -> Ok(normalizeAddresses addresses)

    /// Validates validate resolved host inputs before server processing continues.
    let private validateResolvedHost (resolver: HostAddressResolver) host =
        try
            resolver host |> validateResolvedAddresses host
        with
        | :? Net.Sockets.SocketException -> Error(UnsafeHostRejected host)
        | :? ArgumentException -> Error(UnsafeHostRejected host)

    /// Determines whether exact permitted loopback address.
    let private isExactPermittedLoopbackAddress (address: IPAddress) =
        let normalizedAddress = normalizeAddress address

        normalizedAddress.Equals(IPAddress.Parse("127.0.0.1"))
        || normalizedAddress.Equals(IPAddress.IPv6Loopback)

    /// Determines whether permitted unsafe local host.
    let private isPermittedUnsafeLocalHost (uri: Uri) =
        match tryParseIPAddress uri.Host with
        | Some address -> isExactPermittedLoopbackAddress address
        | None -> isLocalhostName uri.Host

    /// Validates validate public target inputs before server processing continues.
    let private validatePublicTarget (resolver: HostAddressResolver) (uri: Uri) =
        if uri.Scheme <> Uri.UriSchemeHttps then
            Error HttpsRequired
        elif isLocalhostName uri.Host then
            Error(UnsafeHostRejected "localhost")
        else
            match tryParseIPAddress uri.Host with
            | Some address when isUnsafeAddress address -> Error(UnsafeHostRejected(addressForFailure address))
            | Some address -> Ok [| normalizeAddress address |]
            | None -> validateResolvedHost resolver uri.Host

    /// Validates validate local development target inputs before server processing continues.
    let private validateLocalDevelopmentTarget (isDevelopmentHostEnvironment: bool) (configuration: IConfiguration) (request: ValidationRequest) (uri: Uri) =
        if
            not
                (
                    isDevelopmentHostEnvironment
                    && isUnsafeLocalDevelopmentEnabled configuration
                    && request.AcknowledgeUnsafeLocalDevelopment
                )
        then
            Error LocalTargetRequiresDevelopmentAcknowledgement
        elif uri.Scheme <> Uri.UriSchemeHttp
             && uri.Scheme <> Uri.UriSchemeHttps then
            Error(UnsupportedScheme uri.Scheme)
        elif isPermittedUnsafeLocalHost uri then
            match tryParseIPAddress uri.Host with
            | Some address -> Ok [| normalizeAddress address |]
            | None ->
                Ok [| IPAddress.Parse("127.0.0.1")
                      IPAddress.IPv6Loopback |]
        else
            Error(UnsafeHostRejected uri.Host)

    /// Validates validate with resolver inputs before server processing continues.
    let validateWithResolver (resolver: HostAddressResolver) (isDevelopmentHostEnvironment: bool) (configuration: IConfiguration) (request: ValidationRequest) =
        if String.IsNullOrWhiteSpace request.Url then
            Error MissingUrl
        else
            let trimmedUrl = request.Url.Trim()
            let mutable uri = Unchecked.defaultof<Uri>

            if not (Uri.TryCreate(trimmedUrl, UriKind.Absolute, &uri)) then
                Error InvalidUri
            elif uri.Scheme <> Uri.UriSchemeHttp
                 && uri.Scheme <> Uri.UriSchemeHttps then
                Error(UnsupportedScheme uri.Scheme)
            elif not (String.IsNullOrEmpty uri.UserInfo) then
                Error EmbeddedCredentialsRejected
            elif not (String.IsNullOrEmpty uri.Fragment) then
                Error FragmentRejected
            elif String.IsNullOrWhiteSpace uri.Host then
                Error HostRequired
            else
                let validation =
                    match request.RequestedSafety with
                    | OutboundUrlSafety.PublicHttps -> validatePublicTarget resolver uri
                    | OutboundUrlSafety.LocalUnsafeDevOnly -> validateLocalDevelopmentTarget isDevelopmentHostEnvironment configuration request uri

                validation
                |> Result.map (fun resolvedAddresses ->
                    {
                        Uri = uri
                        ScopedUrl = { Url = uri.AbsoluteUri; Safety = request.RequestedSafety }
                        RedirectPolicy = RedirectPolicy.RevalidateEveryRedirect
                        ResolvedAddresses = resolvedAddresses
                    })

    /// Validates validate inputs before server processing continues.
    let validate (hostEnvironment: IHostEnvironment) (configuration: IConfiguration) (request: ValidationRequest) =
        validateWithResolver resolveHostAddresses (isDevelopmentHostEnvironment hostEnvironment) configuration request

    /// Validates validate redirect inputs before server processing continues.
    let validateRedirect (hostEnvironment: IHostEnvironment) (configuration: IConfiguration) (original: ValidatedOutboundUrl) (redirectUri: Uri) =
        if isNull redirectUri
           || not redirectUri.IsAbsoluteUri then
            Error InvalidUri
        else
            validate
                hostEnvironment
                configuration
                {
                    Url = redirectUri.AbsoluteUri
                    RequestedSafety = original.ScopedUrl.Safety
                    AcknowledgeUnsafeLocalDevelopment = original.ScopedUrl.Safety = OutboundUrlSafety.LocalUnsafeDevOnly
                }

    /// Contains Grace Server redaction behavior and supporting helpers.
    module Redaction =

        let private sensitiveQueryNames =
            HashSet<string>(
                [
                    "api_key"
                    "apikey"
                    "sig"
                    "signature"
                    "token"
                    "oauth_token"
                    "access_token"
                    "refresh_token"
                    "id_token"
                    "id_token_hint"
                    "client_assertion"
                    "assertion"
                    "code"
                    "state"
                    "nonce"
                    "samlrequest"
                    "samlresponse"
                    "relaystate"
                    "session_state"
                    "authenticity_token"
                    "csrf_token"
                    "xsrf_token"
                    "request_token"
                    "verification_token"
                    "key"
                    "x-api-key"
                    "secret"
                    "client_secret"
                    "password"
                    "x-amz-algorithm"
                    "x-amz-date"
                    "x-amz-expires"
                    "x-amz-signedheaders"
                    "x-amz-signature"
                    "x-amz-credential"
                    "x-amz-security-token"
                    "x-goog-signature"
                    "x-goog-credential"
                    "x-goog-algorithm"
                    "x-goog-date"
                    "x-goog-expires"
                    "x-goog-signedheaders"
                    "sharedaccesssignature"
                    "st"
                    "se"
                    "sp"
                    "sv"
                    "sr"
                    "skoid"
                    "sktid"
                    "skt"
                    "ske"
                    "sks"
                    "skv"
                ],
                StringComparer.OrdinalIgnoreCase
            )

        /// Computes redact query data used by Grace Server.
        let private redactQuery (query: string) =
            if String.IsNullOrEmpty query then
                String.Empty
            else
                query
                    .TrimStart('?')
                    .Split([| '&'; ';' |], StringSplitOptions.None)
                |> Array.map (fun part ->
                    let separator = part.IndexOf('=')
                    let name = if separator < 0 then part else part.Substring(0, separator)

                    if sensitiveQueryNames.Contains(Uri.UnescapeDataString(name)) then
                        $"{name}=REDACTED"
                    else
                        part)
                |> String.concat "&"

        /// Computes redact uri data used by Grace Server.
        let redactUri (value: string) =
            if String.IsNullOrWhiteSpace value then
                value
            else
                let mutable uri = Unchecked.defaultof<Uri>

                if Uri.TryCreate(value, UriKind.Absolute, &uri) then
                    let builder = UriBuilder(uri)

                    if not (String.IsNullOrEmpty uri.UserInfo) then
                        builder.UserName <- "REDACTED"
                        builder.Password <- String.Empty

                    builder.Path <- "/[redacted-path]"
                    builder.Query <- redactQuery uri.Query
                    builder.Fragment <- String.Empty
                    builder.Uri.AbsoluteUri
                else
                    "[invalid-uri-redacted]"

    /// Contains Grace Server signing behavior and supporting helpers.
    module Signing =

        /// Computes sha256 hex data used by Grace Server.
        let sha256Hex (payload: byte array) =
            let hash = SHA256.HashData payload

            hash
            |> Array.map (fun value -> value.ToString("x2", CultureInfo.InvariantCulture))
            |> String.concat String.Empty

        /// Validates outbound signing fields and hashes the payload into the canonical material covered by the HMAC.
        let createSigningInput (requestId: string) (timestamp: string) (keyId: string) (payload: byte array) =
            if String.IsNullOrWhiteSpace requestId then
                invalidArg (nameof requestId) "A delivery or request id is required for outbound signing."

            if String.IsNullOrWhiteSpace timestamp then
                invalidArg (nameof timestamp) "A timestamp is required for outbound signing."

            if String.IsNullOrWhiteSpace keyId then
                invalidArg (nameof keyId) "A key id or secret version is required for outbound signing."

            if isNull payload then nullArg (nameof payload)

            let payloadHash = sha256Hex payload
            let material = $"{requestId.Trim()}.{timestamp.Trim()}.{keyId.Trim()}.{payloadHash}"

            {
                RequestId = requestId.Trim()
                Timestamp = timestamp.Trim()
                KeyId = keyId.Trim()
                PayloadSha256Hex = payloadHash
                SignedMaterial = Encoding.UTF8.GetBytes material
            }
