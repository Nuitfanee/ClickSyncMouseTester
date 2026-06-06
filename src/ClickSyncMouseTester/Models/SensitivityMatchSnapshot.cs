using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Models;

public class SensitivityMatchSnapshot
{
    private readonly int _availableDeviceCount;

    private readonly RawMouseDeviceInfo _sourceDevice;

    private readonly RawMouseDeviceInfo _targetDevice;

    private readonly bool _isSourceDisconnected;

    private readonly bool _isTargetDisconnected;

    private readonly SensitivityMatchBindingSlot? _pendingBindingSlot;

    private readonly SensitivityMatchBindingIssue _lastBindingIssue;

    private readonly SensitivityMatchBindingSlot? _lastBindingIssueSlot;

    private readonly SensitivityMatchCurrentRoundState _currentRound;

    private readonly IReadOnlyList<SensitivityMatchRoundResult> _completedRounds;

    private readonly SensitivityMatchRoundFailureReason _lastRoundFailureReason;

    private readonly int _lastRoundFailureIndex;

    private readonly double? _finalScale;

    private readonly int? _finalRecommendedTargetDpi;

    private readonly double? _consistencyPercent;

    private readonly SensitivityMatchConsistencyLevel _consistencyLevel;

    private readonly bool _resultsExpired;

    public int AvailableDeviceCount => _availableDeviceCount;

    public bool HasMinimumDeviceCount => _availableDeviceCount >= 2;

    public RawMouseDeviceInfo SourceDevice => _sourceDevice;

    public RawMouseDeviceInfo TargetDevice => _targetDevice;

    public bool HasSourceDevice => _sourceDevice != null;

    public bool HasTargetDevice => _targetDevice != null;

    public bool IsSourceDisconnected => _isSourceDisconnected;

    public bool IsTargetDisconnected => _isTargetDisconnected;

    public SensitivityMatchBindingSlot? PendingBindingSlot => _pendingBindingSlot;

    public bool HasPendingBinding
    {
        get
        {
            SensitivityMatchBindingSlot? pendingBindingSlot = _pendingBindingSlot;
            return pendingBindingSlot.HasValue;
        }
    }

    public SensitivityMatchBindingIssue LastBindingIssue => _lastBindingIssue;

    public SensitivityMatchBindingSlot? LastBindingIssueSlot => _lastBindingIssueSlot;

    public SensitivityMatchCurrentRoundState CurrentRound => _currentRound;

    public bool HasActiveRound => _currentRound != null;

    public IReadOnlyList<SensitivityMatchRoundResult> CompletedRounds => _completedRounds;

    public int CompletedRoundCount => _completedRounds.Count;

    public SensitivityMatchRoundFailureReason LastRoundFailureReason => _lastRoundFailureReason;

    public int LastRoundFailureIndex => _lastRoundFailureIndex;

    public double? FinalScale => _finalScale;

    public int? FinalRecommendedTargetDpi => _finalRecommendedTargetDpi;

    public bool HasFinalRecommendation
    {
        get
        {
            double? finalScale = _finalScale;
            if (finalScale.HasValue)
            {
                int? finalRecommendedTargetDpi = _finalRecommendedTargetDpi;
                return finalRecommendedTargetDpi.HasValue;
            }
            return false;
        }
    }

    public double? ConsistencyPercent => _consistencyPercent;

    public SensitivityMatchConsistencyLevel ConsistencyLevel => _consistencyLevel;

    public bool ResultsExpired => _resultsExpired;

    internal SensitivityMatchSnapshot(int availableDeviceCount, RawMouseDeviceInfo sourceDevice, RawMouseDeviceInfo targetDevice, bool isSourceDisconnected, bool isTargetDisconnected, SensitivityMatchBindingSlot? pendingBindingSlot, SensitivityMatchBindingIssue lastBindingIssue, SensitivityMatchBindingSlot? lastBindingIssueSlot, SensitivityMatchCurrentRoundState currentRound, IReadOnlyList<SensitivityMatchRoundResult> completedRounds, SensitivityMatchRoundFailureReason lastRoundFailureReason, int lastRoundFailureIndex, double? finalScale, int? finalRecommendedTargetDpi, double? consistencyPercent, SensitivityMatchConsistencyLevel consistencyLevel, bool resultsExpired)
    {
        _availableDeviceCount = Math.Max(0, availableDeviceCount);
        _sourceDevice = sourceDevice;
        _targetDevice = targetDevice;
        _isSourceDisconnected = isSourceDisconnected;
        _isTargetDisconnected = isTargetDisconnected;
        _pendingBindingSlot = pendingBindingSlot;
        _lastBindingIssue = lastBindingIssue;
        _lastBindingIssueSlot = lastBindingIssueSlot;
        _currentRound = currentRound;
        _completedRounds = completedRounds ?? Array.Empty<SensitivityMatchRoundResult>();
        _lastRoundFailureReason = lastRoundFailureReason;
        _lastRoundFailureIndex = Math.Max(0, lastRoundFailureIndex);
        _finalScale = finalScale;
        _finalRecommendedTargetDpi = finalRecommendedTargetDpi;
        _consistencyPercent = consistencyPercent;
        _consistencyLevel = consistencyLevel;
        _resultsExpired = resultsExpired;
    }
}





