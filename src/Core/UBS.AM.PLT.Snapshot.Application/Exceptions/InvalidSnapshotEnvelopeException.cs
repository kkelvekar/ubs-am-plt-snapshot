namespace UBS.AM.PLT.Snapshot.Application.Exceptions;

/// <summary>
/// The envelope itself is unusable: a required identity field is null, too long for its
/// column, or carries characters unusable in a blob path; the payload is empty or not
/// syntactically valid JSON; or the payload type is not part of the snapshot type's file
/// contract. Raised before the first write, so nothing durable has been touched when it
/// propagates.
///
/// Non-retryable by nature — the same bytes redelivered produce the same failure. Only the
/// publishing application can fix it by sending a corrected message.
/// </summary>
public sealed class InvalidSnapshotEnvelopeException : SnapshotMessageRejectedException
{
    public const string NullRequiredFieldReason = "NULL_REQUIRED_FIELD";
    public const string FieldTooLongReason = "FIELD_TOO_LONG";
    public const string InvalidFieldCharactersReason = "INVALID_FIELD_CHARACTERS";
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

    /// <summary>
    /// An identity field is longer than the column it lands in. Caught here rather than as a
    /// SQL truncation error mid-write, so the message is rejected before anything durable is
    /// touched instead of failing a write that redelivery could never make succeed.
    /// </summary>
    public static InvalidSnapshotEnvelopeException FieldTooLong(string fieldName, int actualLength, int maxLength)
        => new(
            FieldTooLongReason,
            $"Snapshot message envelope field '{fieldName}' is {actualLength} characters, "
            + $"exceeding the maximum of {maxLength}; rejecting before any write.");

    /// <summary>
    /// A field that forms the blob path carries a character outside the accepted set (letters,
    /// digits, <c>-</c>, <c>_</c>, <c>.</c>). Deliberately narrower than Azure's own blob naming
    /// rules: it keeps path segments from introducing separators or traversal, and sidesteps
    /// service-side naming edge cases entirely.
    /// </summary>
    public static InvalidSnapshotEnvelopeException InvalidFieldCharacters(string fieldName, string value)
        => new(
            InvalidFieldCharactersReason,
            $"Snapshot message envelope field '{fieldName}' value '{value}' contains characters that are not "
            + "letters, digits, '-', '_' or '.', and cannot form a blob path; rejecting before any write.");

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
