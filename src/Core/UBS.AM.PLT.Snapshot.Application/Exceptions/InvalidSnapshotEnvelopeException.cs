namespace UBS.AM.PLT.Snapshot.Application.Exceptions;

/// <summary>
/// The envelope itself is unusable: a required identity field is null, the payload is empty
/// or not syntactically valid JSON, or the payload type is not part of the snapshot type's
/// file contract. Raised before the first write, so nothing durable has been touched when it
/// propagates.
///
/// Non-retryable by nature — the same bytes redelivered produce the same failure. Only the
/// publishing application can fix it by sending a corrected message.
/// </summary>
public sealed class InvalidSnapshotEnvelopeException : SnapshotMessageRejectedException
{
    public const string NullRequiredFieldReason = "NULL_REQUIRED_FIELD";
    public const string EmptyPayloadReason = "EMPTY_PAYLOAD";
    public const string MalformedPayloadJsonReason = "MALFORMED_PAYLOAD_JSON";
    public const string UnexpectedPayloadTypeReason = "UNEXPECTED_PAYLOAD_TYPE";

    private InvalidSnapshotEnvelopeException(string reasonCode, string message)
        : base(reasonCode, message)
    {
    }

    private InvalidSnapshotEnvelopeException(string reasonCode, string message, Exception innerException)
        : base(reasonCode, message, innerException)
    {
    }

    /// <summary>
    /// A field that forms the blob path or the tracking identity arrived as an explicit null.
    /// JSON <c>required</c> only proves the property was present, not that it had a value.
    /// </summary>
    public static InvalidSnapshotEnvelopeException NullRequiredField(string fieldName)
        => new(
            NullRequiredFieldReason,
            $"Snapshot message envelope has a null required field '{fieldName}'; rejecting before any write.");

    public static InvalidSnapshotEnvelopeException EmptyPayload()
        => new(
            EmptyPayloadReason,
            "Snapshot message envelope has a null or empty 'Payload'; rejecting before any write.");

    /// <summary>
    /// The payload is JSON text written to blob verbatim, so a syntactically broken one would
    /// land as an invalid <c>.json</c> file.
    /// </summary>
    public static InvalidSnapshotEnvelopeException MalformedPayloadJson(Exception innerException)
        => new(
            MalformedPayloadJsonReason,
            "Snapshot message payload is not syntactically valid JSON; rejecting before any write.",
            innerException);

    /// <summary>
    /// The payload type is not one of the files this snapshot type is contracted to send.
    /// The expected set is named in the message so the publishing application can see what it
    /// should have sent.
    /// </summary>
    public static InvalidSnapshotEnvelopeException UnexpectedPayloadType(
        string payloadType,
        string snapshotType,
        IEnumerable<string> expectedFiles)
        => new(
            UnexpectedPayloadTypeReason,
            $"Snapshot message payloadType '{payloadType}' is not part of the '{snapshotType}' file contract "
            + $"(expected one of: {string.Join(", ", expectedFiles.Order(StringComparer.Ordinal))}); "
            + "rejecting before any write.");
}
