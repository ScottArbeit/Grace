namespace Grace.Server.Tests.Measurement

open System
open System.Text
open System.Text.RegularExpressions

/// Defines the exact evidence contract and broker observations for the isolated dead-letter witness.
module DeadLetter =

    [<Literal>]
    let MaximumDeliveryCount = 10

    [<Literal>]
    let DeadLetterDeliveryCount = 11

    [<Literal>]
    let private MaximumReasonCharacters = 256

    /// Lists the exact assertion identities required by the isolated dead-letter witness.
    let requiredAssertionIds =
        [|
            "dead-letter.test-subscription-isolated"
            "dead-letter.message-identity-exact"
            "dead-letter.below-maximum-remains-active"
            "dead-letter.dlq-message-observed"
            "dead-letter.delivery-count-eleven"
            "dead-letter.reason-bounded-nonempty"
            "dead-letter.production-manifest-telemetry-unchanged"
            "dead-letter.cleanup-complete"
            "dead-letter.evidence-integrity"
        |]

    /// Requires one exact witness identity instead of accepting a count or another subscription message.
    let identityMatches expectedMessageId observedMessageId =
        not (String.IsNullOrWhiteSpace expectedMessageId)
        && expectedMessageId.Equals(observedMessageId, StringComparison.Ordinal)

    /// Derives the DLQ-observed assertion only from the exact identity returned by the broker transition.
    let dlqMessageObserved expectedMessageId returnedMessageId = identityMatches expectedMessageId returnedMessageId

    /// Requires the last active delivery to remain outside the DLQ at the configured maximum.
    let belowMaximumRemainsActive expectedMessageId activeMessageId activeDeliveryCount (deadLetterMessageIds: string array) =
        identityMatches expectedMessageId activeMessageId
        && activeDeliveryCount = MaximumDeliveryCount
        && deadLetterMessageIds
           |> Array.exists (identityMatches expectedMessageId)
           |> not

    /// Projects a broker-owned reason into bounded printable diagnostics with credential-shaped values redacted.
    let boundedBrokerReason (reason: string) =
        let source = if isNull reason then String.Empty else reason.Trim()

        let redacted =
            Regex.Replace(source, "(?i)(Endpoint|SharedAccessKey|SharedAccessSignature|Password|Token)=([^;\\s]+)", "$1=***", RegexOptions.CultureInvariant)

        let builder = StringBuilder(min redacted.Length MaximumReasonCharacters)
        let mutable index = 0

        while index < redacted.Length
              && builder.Length < MaximumReasonCharacters do
            let character = redacted[index]

            builder.Append(if character >= ' ' && character <= '~' then character else '?')
            |> ignore

            index <- index + 1

        builder.ToString()

    /// Requires broker-owned dead-letter diagnostics to remain nonempty after bounded redaction.
    let brokerReasonPasses reason =
        let boundedReason = boundedBrokerReason reason

        not (String.IsNullOrWhiteSpace boundedReason)
        && boundedReason.Length <= MaximumReasonCharacters

    /// Requires the exact witness to appear in the DLQ on delivery eleven with inspectable broker diagnostics.
    let deadLetterObservationPasses expectedMessageId observedMessageId deliveryCount brokerReason =
        identityMatches expectedMessageId observedMessageId
        && deliveryCount = DeadLetterDeliveryCount
        && brokerReasonPasses brokerReason

    /// Requires terminal cleanup to remove the exact witness from both active and dead-letter subqueues.
    let cleanupComplete expectedMessageId (activeMessageIds: string array) (deadLetterMessageIds: string array) =
        Array.append activeMessageIds deadLetterMessageIds
        |> Array.exists (identityMatches expectedMessageId)
        |> not
