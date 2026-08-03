using System.Buffers;
using System.Text;
using System.Text.Json;

namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;

/// <summary>
/// Composes the all-payloads response: one JSON object keyed by payloadType, each value the
/// stored blob text embedded verbatim.
/// <para>
/// <see cref="Utf8JsonWriter.WriteRawValue(string, bool)"/> is what keeps AGENTS.md invariant 5
/// intact here — it validates the stored text is syntactically well-formed JSON and then copies
/// those exact bytes into the output. Nothing is deserialised into a DTO and nothing is
/// re-serialised, so key order, number formatting (trailing zeros included) and internal
/// whitespace all survive byte-for-byte. Do not replace this with a parse-and-write.
/// </para>
/// A WriteRawValue failure means a stored blob is not valid JSON — corruption of data the write
/// path syntax-checked before storing. That is a server fault, so the exception is deliberately
/// left to propagate to the global handler as a 500 rather than being turned into a corrupt 200.
/// </summary>
public static class SnapshotPayloadDocumentComposer
{
    public static string Compose(IReadOnlyList<SnapshotPayloadFile> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var payload in payloads)
            {
                writer.WritePropertyName(payload.PayloadType);
                writer.WriteRawValue(payload.Json);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
