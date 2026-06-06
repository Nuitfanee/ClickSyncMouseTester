namespace ClickSyncMouseTester.Models;

public sealed class MousePerformanceSessionArchive
{
    private readonly MousePerformanceSessionSourceMode _sourceMode;

    private readonly MousePerformanceSessionMetadata _metadata;

    private readonly MousePerformanceSnapshot _snapshot;

    private readonly string _sourceFilePath;

    public MousePerformanceSessionSourceMode SourceMode => _sourceMode;

    public MousePerformanceSessionMetadata Metadata => _metadata;

    public MousePerformanceSnapshot Snapshot => _snapshot;

    public string SourceFilePath => _sourceFilePath;

    public bool HasData
    {
        get
        {
            if (_snapshot != null)
            {
                return _snapshot.HasData;
            }
            return false;
        }
    }

    public string DisplayName
    {
        get
        {
            if (_metadata == null || string.IsNullOrWhiteSpace(_metadata.DisplayName))
            {
                return "Mouse Session";
            }
            return _metadata.DisplayName;
        }
    }

    public MousePerformanceSessionArchive(MousePerformanceSessionSourceMode sourceMode, MousePerformanceSessionMetadata metadata, MousePerformanceSnapshot snapshot, string sourceFilePath = null)
    {
        _sourceMode = sourceMode;
        _metadata = metadata ?? new MousePerformanceSessionMetadata(string.Empty, string.Empty, null, null, 0, isVirtual: false, string.Empty);
        _snapshot = snapshot;
        _sourceFilePath = sourceFilePath ?? string.Empty;
    }
}





