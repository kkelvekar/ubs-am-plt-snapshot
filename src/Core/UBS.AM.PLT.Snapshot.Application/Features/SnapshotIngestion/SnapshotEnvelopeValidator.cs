using System.Text.Json;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Pre-write guards on the message envelope, per the message contract in design §4.
/// Both run before the first write, so a rejected message leaves nothing durable behind.
/// </summary>
public static class SnapshotEnvelopeValidator
{
    public static void ValidateEnvelope(SnapshotMessage message)
    {
        // Only the fields that form the blob path and tracking identity are checked —
        // a null in any of them corrupts a write. Deserialisation guarantees presence;
        // this guards against present-but-null. (Non-nullable reference-type annotations
        // are not enforced at runtime, so this check is real, not redundant.)
        ThrowIfNull(message.SnapshotId, nameof(message.SnapshotId));
        ThrowIfNull(message.AccountId, nameof(message.AccountId));
        ThrowIfNull(message.SnapshotType, nameof(message.SnapshotType));
        ThrowIfNull(message.PayloadType, nameof(message.PayloadType));

        // The same four fields are the only message-derived values that land in a bounded
        // (fixed-width VARCHAR) column or in a blob path segment. Bounding them here is what
        // keeps every POST-write failure uniformly retryable: an over-long or unusable value
        // would otherwise surface as a SQL truncation or blob-naming error on a write that
        // redelivery could never make succeed, and the consumer would crash-loop on it.
        ThrowIfTooLong(message.SnapshotId, nameof(message.SnapshotId), SnapshotFieldLimits.SnapshotIdMaxLength);
        ThrowIfTooLong(message.AccountId, nameof(message.AccountId), SnapshotFieldLimits.AccountIdMaxLength);
        ThrowIfTooLong(message.SnapshotType, nameof(message.SnapshotType), SnapshotFieldLimits.SnapshotTypeMaxLength);
        ThrowIfTooLong(message.PayloadType, nameof(message.PayloadType), SnapshotFieldLimits.PayloadTypeMaxLength);

        // All four also compose the blob path (see SnapshotBlobPath): snapshotType names the
        // top-level folder, accountId and snapshotId their own segments, payloadType the
        // filename. A separator, a traversal sequence or an exotic character in any of them
        // would land the blob somewhere other than its snapshot folder.
        ThrowIfNotPathSafe(message.SnapshotId, nameof(message.SnapshotId));
        ThrowIfNotPathSafe(message.AccountId, nameof(message.AccountId));
        ThrowIfNotPathSafe(message.SnapshotType, nameof(message.SnapshotType));
        ThrowIfNotPathSafe(message.PayloadType, nameof(message.PayloadType));

        // The payload now arrives as a string of already-serialised JSON, so an empty or
        // syntactically broken payload would otherwise be written to blob as an invalid
        // .json file. Reject it here, before the first write.
        if (string.IsNullOrWhiteSpace(message.Payload))
        {
            throw InvalidSnapshotEnvelopeException.EmptyPayload();
        }

        // SYNTAX-ONLY well-formedness check. The document is disposed immediately and no
        // field inside it is ever read: the payload stays opaque (invariant #5), and the
        // string written to blob is always `message.Payload` verbatim, never anything
        // re-serialised from this parse.
        try
        {
            using var syntaxCheckOnly = JsonDocument.Parse(message.Payload);
        }
        catch (JsonException ex)
        {
            // Re-raised as a rejection so the consumer commits past it: the same bytes
            // redelivered parse identically, so retrying only blocks the partition.
            throw InvalidSnapshotEnvelopeException.MalformedPayloadJson(ex);
        }

        static void ThrowIfNull(string? value, string fieldName)
        {
            if (value is null)
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
                // ASCII letters and digits only — char.IsLetterOrDigit would accept the whole
                // Unicode letter range, which the VARCHAR columns cannot round-trip anyway.
                var accepted = character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                    or '-' or '_' or '.';
                if (!accepted)
                {
                    throw InvalidSnapshotEnvelopeException.InvalidFieldCharacters(fieldName, value);
                }
            }

            // '.' is accepted above (payload types and account ids may legitimately carry one),
            // which on its own would let a traversal sequence through the character loop.
            if (value.Contains("..", StringComparison.Ordinal))
            {
                throw InvalidSnapshotEnvelopeException.InvalidFieldCharacters(fieldName, value);
            }
        }
    }

    /// <summary>
    /// Rejects a payload that is not part of the snapshot type's file contract — the expected
    /// payloads are exactly the required files.
    /// </summary>
    /// <remarks>
    /// Runs before the first write on purpose: an out-of-contract file that reached the blob
    /// store would sit in the snapshot folder forever, and once its name entered
    /// received_files it would appear in every response for the rest of the snapshot's life.
    ///
    /// An unknown snapshotType cannot be checked here — there is no expected-file list to
    /// check against. That case keeps its existing behaviour unchanged (design decision: no
    /// pre-write config check): the blob and tracking row are written, and the completeness
    /// step then fails to resolve the list. It is not the publisher's fault and is not
    /// rejected.
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
