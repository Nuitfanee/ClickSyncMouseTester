using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Models;

public class MousePerformanceSnapshot
{
    private readonly MousePerformanceSessionStatus _status;

    private readonly bool _isLocked;

    private readonly bool _isFinalized;

    private readonly bool _canContinue;

    private readonly string _sessionDeviceId;

    private readonly double? _effectiveCpi;

    private readonly bool _canComputeVelocity;

    private readonly MousePerformanceSummary _summary;

    private readonly IReadOnlyList<MousePerformanceEvent> _events;

    private readonly int _sessionRevision;

    private readonly int _eventCount;

    private readonly MousePerformanceDataQuality _dataQuality;

    private readonly IReadOnlyList<MousePerformanceSessionSegment> _sessionSegments;

    public MousePerformanceSessionStatus Status => _status;

    public bool IsLocked => _isLocked;

    public bool IsFinalized => _isFinalized;

    public bool CanContinue => _canContinue;

    public string SessionDeviceId => _sessionDeviceId;

    public double? EffectiveCpi => _effectiveCpi;

    public bool CanComputeVelocity => _canComputeVelocity;

    public MousePerformanceSummary Summary => _summary;

    public IReadOnlyList<MousePerformanceEvent> Events => _events;

    public int SessionRevision => _sessionRevision;

    public int EventCount => _eventCount;

    public MousePerformanceDataQuality DataQuality => _dataQuality;

    public IReadOnlyList<MousePerformanceSessionSegment> SessionSegments => _sessionSegments;

    public bool HasData => _eventCount > 0;

    public MousePerformanceSnapshot(MousePerformanceSessionStatus status, bool isLocked, bool isFinalized, bool canContinue, string sessionDeviceId, double? effectiveCpi, bool canComputeVelocity, MousePerformanceSummary summary, IReadOnlyList<MousePerformanceEvent> events, int sessionRevision, int eventCount, MousePerformanceDataQuality dataQuality, IReadOnlyList<MousePerformanceSessionSegment> sessionSegments)
    {
        _status = status;
        _isLocked = isLocked;
        _isFinalized = isFinalized;
        _canContinue = canContinue;
        _sessionDeviceId = sessionDeviceId ?? string.Empty;
        _effectiveCpi = effectiveCpi;
        _canComputeVelocity = canComputeVelocity;
        _summary = summary ?? new MousePerformanceSummary(0, 0L, 0L, 0.0, null, null, null, null, null, null);
        _events = events ?? Array.Empty<MousePerformanceEvent>();
        _sessionRevision = Math.Max(0, sessionRevision);
        _eventCount = Math.Max(0, eventCount);
        _dataQuality = dataQuality;
        _sessionSegments = sessionSegments ?? Array.Empty<MousePerformanceSessionSegment>();
    }
}





