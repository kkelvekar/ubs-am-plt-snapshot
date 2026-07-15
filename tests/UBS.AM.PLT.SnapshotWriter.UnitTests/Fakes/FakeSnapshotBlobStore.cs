using UBS.AM.PLT.SnapshotWriter.Application.Contracts.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;

public sealed class FakeSnapshotBlobStore : ISnapshotBlobStore
{
    private readonly List<(SnapshotMessage Message, string RootPath)> _written = [];
    private readonly List<string> _headerReadsFor = [];

    public IReadOnlyList<(SnapshotMessage Message, string RootPath)> Written => _written;

    public IReadOnlyList<string> HeaderReadsFor => _headerReadsFor;

    public Exception? ThrowOnWrite { get; set; }

    public Exception? ThrowOnReadHeader { get; set; }

    public string HeaderJson { get; set; } = "{}";

    public Task WriteAsync(SnapshotMessage message, string rootPath, CancellationToken cancellationToken)
    {
        if (ThrowOnWrite is not null)
        {
            throw ThrowOnWrite;
        }

        _written.Add((message, rootPath));
        return Task.CompletedTask;
    }

    public Task<string> ReadHeaderAsync(string adlsRootPath, CancellationToken cancellationToken)
    {
        if (ThrowOnReadHeader is not null)
        {
            throw ThrowOnReadHeader;
        }

        _headerReadsFor.Add(adlsRootPath);
        return Task.FromResult(HeaderJson);
    }
}
