using System.Text.Json;
using Microsoft.Extensions.Logging;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Application;

/// <summary>
/// Orchestrates the strict write order per message:
/// blob write → tracking upsert → completeness check → index UPSERT. Any failure
/// propagates unchanged so the consumer never commits the offset (recovery is forward,
/// via redelivery).
/// </summary>
public sealed class SnapshotMessageHandler : ISnapshotMessageHandler
{
    private readonly ISnapshotBlobStore _blobStore;
    private readonly ISnapshotTrackingStore _trackingStore;
    private readonly IRequiredFilesProvider _requiredFilesProvider;
    private readonly ISnapshotIndexStore _indexStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SnapshotMessageHandler> _logger;

    public SnapshotMessageHandler(
        ISnapshotBlobStore blobStore,
        ISnapshotTrackingStore trackingStore,
        IRequiredFilesProvider requiredFilesProvider,
        ISnapshotIndexStore indexStore,
        TimeProvider timeProvider,
        ILogger<SnapshotMessageHandler> logger)
    {
        _blobStore = blobStore;
        _trackingStore = trackingStore;
        _requiredFilesProvider = requiredFilesProvider;
        _indexStore = indexStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        // Guard: the JSON `required` check on the envelope only proves each identity field
        // was present in the message — not that it was non-null (an explicit
        // "snapshotId": null passes deserialisation with a null value). A null identity
        // field would corrupt the blob path (e.g. ".../snapshotId=/...") and the tracking
        // row, so reject it before the first write. Throwing routes it through the
        // consumer's existing no-commit / seek-back / retry / alert path, identical to a
        // malformed envelope — recovery is forward, never a silent skip.
        ValidateEnvelope(message);

        // The root folder is pinned to the FIRST payload's arrival time for this
        // snapshotId and reused by every subsequent (or redelivered) payload, so a
        // snapshot whose publish timestamps straddle a month/year boundary never splits
        // across two folders. The lookup is read-only (mutates nothing) — the write order
        // below still starts at the blob write. Kafka's accountId partitioning processes
        // a snapshot's messages sequentially on one consumer (see SqlSnapshotTrackingStore),
        // so no locking is needed around it.
        var rootPath = await _trackingStore.GetRootPathAsync(message.SnapshotId, cancellationToken)
            ?? SnapshotBlobPath.RootFolder(message, _timeProvider.GetUtcNow());
        await _blobStore.WriteAsync(message, rootPath, cancellationToken);
        var tracking = await _trackingStore.UpsertReceivedAsync(message, rootPath, cancellationToken);

        // Guard: stray redelivery of a file long after the snapshot already completed (or
        // failed) must do nothing beyond steps 1-2 above — no re-fetch, re-upsert or
        // re-touch of a row that is no longer RECEIVING.
        if (tracking.Status == SnapshotTrackingStatus.Receiving)
        {
            var required = _requiredFilesProvider.GetRequiredFiles(message.SnapshotType);
            if (SnapshotCompleteness.IsComplete(tracking.ReceivedFiles, required))
            {
                var headerJson = await _blobStore.ReadHeaderAsync(tracking.AdlsRootPath, cancellationToken);
                var eventType = ExtractEventType(headerJson, message);

                var indexEntry = BuildIndexEntry(message, tracking, headerJson, eventType);

                // Index UPSERT must precede the status flip: if the index write fails,
                // tracking must still read RECEIVING on redelivery so this guard retries.
                await _indexStore.UpsertAsync(indexEntry, cancellationToken);
                await _trackingStore.MarkCompleteAsync(message.SnapshotId, cancellationToken);

                // Distinct completion event: the business-critical moment the snapshot
                // becomes visible in the audit UI. Answers "when did snapshot X complete".
                _logger.LogInformation(
                    "Snapshot complete: index row upserted and tracking marked complete snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} adlsRootPath={AdlsRootPath} eventType={EventType}",
                    message.SnapshotId,
                    message.AccountId,
                    message.PayloadType,
                    tracking.AdlsRootPath,
                    eventType);
            }
        }

        _logger.LogInformation(
            "Wrote snapshot payload blob and upserted tracking snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} rootPath={RootPath} receivedFileCount={ReceivedFileCount}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType,
            rootPath,
            tracking.ReceivedFiles.Count);
    }

    private static void ValidateEnvelope(SnapshotMessage message)
    {
        // Only the fields that form the blob path and tracking identity are checked —
        // a null in any of them corrupts a write. Deserialisation guarantees presence;
        // this guards against present-but-null. (Non-nullable reference-type annotations
        // are not enforced at runtime, so this check is real, not redundant.)
        ThrowIfNull(message.SnapshotId, nameof(message.SnapshotId));
        ThrowIfNull(message.AccountId, nameof(message.AccountId));
        ThrowIfNull(message.SnapshotType, nameof(message.SnapshotType));
        ThrowIfNull(message.PayloadType, nameof(message.PayloadType));

        // The payload now arrives as a string of already-serialised JSON, so an empty or
        // syntactically broken payload would otherwise be written to blob as an invalid
        // .json file. Reject it here, before the first write.
        if (string.IsNullOrWhiteSpace(message.Payload))
        {
            throw new ArgumentException(
                $"Snapshot message envelope has a null or empty '{nameof(message.Payload)}'; rejecting before any write.",
                nameof(message));
        }

        // SYNTAX-ONLY well-formedness check. The document is disposed immediately and no
        // field inside it is ever read: the payload stays opaque (invariant #5), and the
        // string written to blob is always `message.Payload` verbatim, never anything
        // re-serialised from this parse. A JsonException here propagates like any other
        // failure — no write has happened yet.
        using var syntaxCheckOnly = JsonDocument.Parse(message.Payload);

        static void ThrowIfNull(string? value, string fieldName)
        {
            if (value is null)
            {
                throw new ArgumentException(
                    $"Snapshot message envelope has a null required field '{fieldName}'; rejecting before any write.",
                    nameof(message));
            }
        }
    }

    /// <summary>
    /// Reads the ONE header value this service needs: <c>eventType</c>, which has its own
    /// filterable SQL column. The header otherwise stays opaque — the document is disposed
    /// immediately, no other field is ever read, and nothing from this parse is written
    /// (the persisted display data is always the header text verbatim).
    /// </summary>
    /// <remarks>
    /// Malformed header JSON throws out of <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/>
    /// and propagates: no index row, no MarkComplete, no offset commit, recovery by
    /// redelivery. A well-formed header that simply lacks a usable <c>eventType</c> is an
    /// upstream contract breach, not a transport failure — it is logged and the row is
    /// still written, because retrying it forever would never fix it.
    /// </remarks>
    private string ExtractEventType(string headerJson, SnapshotMessage message)
    {
        using var header = JsonDocument.Parse(headerJson);

        if (header.RootElement.ValueKind == JsonValueKind.Object)
        {
            // JsonSerializerDefaults.Web used to bind eventType and EventType alike;
            // JsonDocument.TryGetProperty is case-SENSITIVE, so match explicitly or every
            // PascalCase header silently regresses to an empty EventType. On duplicate keys
            // differing only by case, document order decides — deterministic across redeliveries.
            foreach (var property in header.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "eventType", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } eventType)
                {
                    return eventType;
                }

                break;
            }
        }

        _logger.LogWarning(
            "Header has no usable eventType; writing the index row with an empty EventType snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType);

        return string.Empty;
    }

    private static SnapshotIndexEntity BuildIndexEntry(
        SnapshotMessage message,
        SnapshotTrackingEntity tracking,
        string headerJson,
        string eventType)
        => new()
        {
            SnapshotId = message.SnapshotId,
            AccountId = message.AccountId,
            SnapshotDate = tracking.FirstReceivedAt,
            EventType = eventType,
            AdlsPath = tracking.AdlsRootPath,
            DisplayData = headerJson,
        };
}
