using System.Text.Json;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Pre-write guards on the message envelope, per the message contract in design §4. Both
/// guards run before the first write, so a rejected message never leaves a blob in the
/// snapshot folder or a row in the index.
/// </summary>
public static class SnapshotEnvelopeValidator
{
    /// <summary>
    /// Validates the identity fields and the payload text.
    /// </summary>
    /// <exception cref="InvalidSnapshotEnvelopeException">
    /// An identity field is null, too long or not path-safe, or the payload is empty or not
    /// syntactically valid JSON.
    /// </exception>
    public static void ValidateEnvelope(SnapshotMessage message)
    {
        // Non-nullable annotations are not enforced at runtime: an explicit "snapshotId": null
        // deserialises fine, and some publishers send "" instead of omitting a value or
        // sending null. Either would corrupt the blob path and the tracking row.
        ThrowIfNullOrEmpty(message.SnapshotId, nameof(message.SnapshotId));
        ThrowIfNullOrEmpty(message.AccountId, nameof(message.AccountId));
        ThrowIfNullOrEmpty(message.SnapshotType, nameof(message.SnapshotType));
        ThrowIfNullOrEmpty(message.PayloadType, nameof(message.PayloadType));

        // Bounded before the first write so an over-long value is rejected here rather than
        // surfacing as a SQL truncation error on a write redelivery could never make succeed.
        ThrowIfTooLong(message.SnapshotId, nameof(message.SnapshotId), SnapshotFieldLimits.SnapshotIdMaxLength);
        ThrowIfTooLong(message.AccountId, nameof(message.AccountId), SnapshotFieldLimits.AccountIdMaxLength);
        ThrowIfTooLong(message.SnapshotType, nameof(message.SnapshotType), SnapshotFieldLimits.SnapshotTypeMaxLength);
        ThrowIfTooLong(message.PayloadType, nameof(message.PayloadType), SnapshotFieldLimits.PayloadTypeMaxLength);

        // All four compose the blob path (see SnapshotBlobPath), so a separator or traversal
        // sequence in any of them would land the blob outside its snapshot folder.
        ThrowIfNotPathSafe(message.SnapshotId, nameof(message.SnapshotId));
        ThrowIfNotPathSafe(message.AccountId, nameof(message.AccountId));
        ThrowIfNotPathSafe(message.SnapshotType, nameof(message.SnapshotType));
        ThrowIfNotPathSafe(message.PayloadType, nameof(message.PayloadType));

        if (string.IsNullOrWhiteSpace(message.Payload))
        {
            throw InvalidSnapshotEnvelopeException.EmptyPayload();
        }

        // Syntax-only check: the document is disposed immediately and no field inside it is
        // ever read, so the payload stays opaque and the blob is always written from
        // message.Payload verbatim, never from anything this parse produced.
        try
        {
            using var syntaxCheckOnly = JsonDocument.Parse(message.Payload);
        }
        catch (JsonException ex)
        {
            throw InvalidSnapshotEnvelopeException.MalformedPayloadJson(ex);
        }

        static void ThrowIfNullOrEmpty(string? value, string fieldName)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw InvalidSnapshotEnvelopeException.NullRequiredField(fieldName);
            }
        }

        static void ThrowIfTooLong(string value, string fieldName, int maxLength)
        {
            if (value.Length > maxLength)
            {
                throw InvalidSnapshotEnvelopeException.FieldTooLong(fieldName, value.Length, maxLength);
            }
        }

        static void ThrowIfNotPathSafe(string value, string fieldName)
        {
            foreach (var character in value)
            {
                // ASCII only: char.IsLetterOrDigit would accept the whole Unicode letter
                // range, which the VARCHAR columns cannot round-trip.
                var accepted = character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                    or '-' or '_' or '.';
                if (!accepted)
                {
                    throw InvalidSnapshotEnvelopeException.InvalidFieldCharacters(fieldName, value);
                }
            }

            // '.' is accepted above, so the character loop alone would let a traversal
            // sequence through.
            if (value.Contains("..", StringComparison.Ordinal))
            {
                throw InvalidSnapshotEnvelopeException.InvalidFieldCharacters(fieldName, value);
            }
        }
    }

    /// <summary>
    /// Rejects a payload that is not part of the snapshot type's file contract; the expected
    /// payloads are exactly the required files.
    /// </summary>
    /// <remarks>
    /// Runs before the first write: an out-of-contract file that reached the blob store would
    /// sit in the snapshot folder forever, and its name in received_files would appear in
    /// every subsequent response for that snapshot.
    ///
    /// An unknown snapshotType is not rejected here — there is no expected-file list to check
    /// against, and it is not the publisher's fault. Such a message follows the normal write
    /// path and the completeness step then fails to resolve the required files.
    /// </remarks>
    public static void ValidatePayloadType(SnapshotMessage message, IRequiredFilesProvider requiredFilesProvider)
    {
        IReadOnlySet<string> expectedFiles;
        try
        {
            expectedFiles = requiredFilesProvider.GetRequiredFiles(message.SnapshotType);
        }
        catch (KeyNotFoundException)
        {
            return;
        }

        if (!expectedFiles.Contains(SnapshotBlobPath.FileName(message.PayloadType)))
        {
            throw InvalidSnapshotEnvelopeException.UnexpectedPayloadType(
                message.PayloadType,
                message.SnapshotType,
                expectedFiles);
        }
    }
}
