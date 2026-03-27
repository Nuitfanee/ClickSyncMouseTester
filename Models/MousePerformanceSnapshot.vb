Imports System.Diagnostics

Namespace Models
    Public Enum MousePerformanceSessionStatus
        Ready
        Collecting
        Paused
        Stopped
        NoDevice
        DeviceDisconnected
    End Enum

    Public Enum MousePerformancePacketKind
        Motion = 0
        MotionWithButton = 1
        MotionWithWheel = 2
        ButtonOnly = 3
        WheelOnly = 4
    End Enum

    Public Enum MousePerformanceDataQualityLevel
        None = 0
        Good = 1
        Degraded = 2
    End Enum

    Public NotInheritable Class MousePerformanceAnalysisOptions
        Public Shared ReadOnly [Default] As New MousePerformanceAnalysisOptions(True, True, 12.0, 3, 30.0)

        Public Sub New(excludeButtonOnly As Boolean,
                       excludeWheelOnly As Boolean,
                       trendWindowMs As Double,
                       minimumTrendSamples As Integer,
                       chartRefreshMaxHz As Double)
            Me.ExcludeButtonOnly = excludeButtonOnly
            Me.ExcludeWheelOnly = excludeWheelOnly
            Me.TrendWindowMs = If(Double.IsNaN(trendWindowMs) OrElse Double.IsInfinity(trendWindowMs), 12.0, Math.Max(1.0, trendWindowMs))
            Me.MinimumTrendSamples = Math.Max(3, minimumTrendSamples)
            Me.ChartRefreshMaxHz = If(Double.IsNaN(chartRefreshMaxHz) OrElse Double.IsInfinity(chartRefreshMaxHz), 30.0, Math.Max(1.0, chartRefreshMaxHz))
        End Sub

        Public ReadOnly Property ExcludeButtonOnly As Boolean
        Public ReadOnly Property ExcludeWheelOnly As Boolean
        Public ReadOnly Property TrendWindowMs As Double
        Public ReadOnly Property MinimumTrendSamples As Integer
        Public ReadOnly Property ChartRefreshMaxHz As Double
    End Class

    Public Class MousePerformanceEvent
        Private ReadOnly _deltaX As Integer
        Private ReadOnly _deltaY As Integer
        Private ReadOnly _buttonFlags As UShort
        Private ReadOnly _timestampTicks As Long
        Private ReadOnly _logicalTicks As Long
        Private ReadOnly _packetKind As MousePerformancePacketKind
        Private ReadOnly _sessionCumulativeX As Long
        Private ReadOnly _sessionCumulativeY As Long

        Public Sub New(deltaX As Integer,
                       deltaY As Integer,
                       buttonFlags As UShort,
                       timestampTicks As Long,
                       logicalTicks As Long,
                       packetKind As MousePerformancePacketKind,
                       sessionCumulativeX As Long,
                       sessionCumulativeY As Long)
            _deltaX = deltaX
            _deltaY = deltaY
            _buttonFlags = buttonFlags
            _timestampTicks = timestampTicks
            _logicalTicks = Math.Max(0L, logicalTicks)
            _packetKind = packetKind
            _sessionCumulativeX = sessionCumulativeX
            _sessionCumulativeY = sessionCumulativeY
        End Sub

        Public ReadOnly Property DeltaX As Integer
            Get
                Return _deltaX
            End Get
        End Property

        Public ReadOnly Property DeltaY As Integer
            Get
                Return _deltaY
            End Get
        End Property

        Public ReadOnly Property ButtonFlags As UShort
            Get
                Return _buttonFlags
            End Get
        End Property

        Public ReadOnly Property TimestampTicks As Long
            Get
                Return _timestampTicks
            End Get
        End Property

        Public ReadOnly Property LogicalTicks As Long
            Get
                Return _logicalTicks
            End Get
        End Property

        Public ReadOnly Property PacketKind As MousePerformancePacketKind
            Get
                Return _packetKind
            End Get
        End Property

        Public ReadOnly Property SessionCumulativeX As Long
            Get
                Return _sessionCumulativeX
            End Get
        End Property

        Public ReadOnly Property SessionCumulativeY As Long
            Get
                Return _sessionCumulativeY
            End Get
        End Property

        Public ReadOnly Property TimestampMs As Double
            Get
                Return _logicalTicks * 1000.0 / Stopwatch.Frequency
            End Get
        End Property
    End Class

    Public Class MousePerformanceSummary
        Private ReadOnly _eventCount As Integer
        Private ReadOnly _sumX As Long
        Private ReadOnly _sumY As Long
        Private ReadOnly _pathCounts As Double
        Private ReadOnly _cpi As Nullable(Of Double)
        Private ReadOnly _sumXCm As Nullable(Of Double)
        Private ReadOnly _sumYCm As Nullable(Of Double)
        Private ReadOnly _pathCm As Nullable(Of Double)

        Public Sub New(eventCount As Integer,
                       sumX As Long,
                       sumY As Long,
                       pathCounts As Double,
                       cpi As Nullable(Of Double),
                       sumXCm As Nullable(Of Double),
                       sumYCm As Nullable(Of Double),
                       pathCm As Nullable(Of Double))
            _eventCount = Math.Max(0, eventCount)
            _sumX = sumX
            _sumY = sumY
            _pathCounts = pathCounts
            _cpi = cpi
            _sumXCm = sumXCm
            _sumYCm = sumYCm
            _pathCm = pathCm
        End Sub

        Public ReadOnly Property EventCount As Integer
            Get
                Return _eventCount
            End Get
        End Property

        Public ReadOnly Property SumX As Long
            Get
                Return _sumX
            End Get
        End Property

        Public ReadOnly Property SumY As Long
            Get
                Return _sumY
            End Get
        End Property

        Public ReadOnly Property PathCounts As Double
            Get
                Return _pathCounts
            End Get
        End Property

        Public ReadOnly Property Cpi As Nullable(Of Double)
            Get
                Return _cpi
            End Get
        End Property

        Public ReadOnly Property SumXCm As Nullable(Of Double)
            Get
                Return _sumXCm
            End Get
        End Property

        Public ReadOnly Property SumYCm As Nullable(Of Double)
            Get
                Return _sumYCm
            End Get
        End Property

        Public ReadOnly Property PathCm As Nullable(Of Double)
            Get
                Return _pathCm
            End Get
        End Property

        Public ReadOnly Property HasDistanceConversion As Boolean
            Get
                Return _sumXCm.HasValue AndAlso _sumYCm.HasValue AndAlso _pathCm.HasValue
            End Get
        End Property
    End Class

    Public Class MousePerformanceDataQuality
        Private ReadOnly _droppedPacketCount As Integer
        Private ReadOnly _filteredButtonOnlyCount As Integer
        Private ReadOnly _filteredWheelOnlyCount As Integer
        Private ReadOnly _outOfOrderTimestampCount As Integer
        Private ReadOnly _zeroIntervalCount As Integer
        Private ReadOnly _qualityLevel As MousePerformanceDataQualityLevel

        Public Sub New(droppedPacketCount As Integer,
                       filteredButtonOnlyCount As Integer,
                       filteredWheelOnlyCount As Integer,
                       outOfOrderTimestampCount As Integer,
                       zeroIntervalCount As Integer,
                       qualityLevel As MousePerformanceDataQualityLevel)
            _droppedPacketCount = Math.Max(0, droppedPacketCount)
            _filteredButtonOnlyCount = Math.Max(0, filteredButtonOnlyCount)
            _filteredWheelOnlyCount = Math.Max(0, filteredWheelOnlyCount)
            _outOfOrderTimestampCount = Math.Max(0, outOfOrderTimestampCount)
            _zeroIntervalCount = Math.Max(0, zeroIntervalCount)
            _qualityLevel = qualityLevel
        End Sub

        Public ReadOnly Property DroppedPacketCount As Integer
            Get
                Return _droppedPacketCount
            End Get
        End Property

        Public ReadOnly Property FilteredButtonOnlyCount As Integer
            Get
                Return _filteredButtonOnlyCount
            End Get
        End Property

        Public ReadOnly Property FilteredWheelOnlyCount As Integer
            Get
                Return _filteredWheelOnlyCount
            End Get
        End Property

        Public ReadOnly Property OutOfOrderTimestampCount As Integer
            Get
                Return _outOfOrderTimestampCount
            End Get
        End Property

        Public ReadOnly Property ZeroIntervalCount As Integer
            Get
                Return _zeroIntervalCount
            End Get
        End Property

        Public ReadOnly Property QualityLevel As MousePerformanceDataQualityLevel
            Get
                Return _qualityLevel
            End Get
        End Property

        Public ReadOnly Property TotalFilteredCount As Integer
            Get
                Return _filteredButtonOnlyCount + _filteredWheelOnlyCount
            End Get
        End Property
    End Class

    Public Class MousePerformanceDiagnostics
        Private ReadOnly _motionEventCount As Integer
        Private ReadOnly _medianIntervalMs As Nullable(Of Double)
        Private ReadOnly _p95IntervalMs As Nullable(Of Double)
        Private ReadOnly _medianFrequencyHz As Nullable(Of Double)
        Private ReadOnly _peakVelocityCmPerSecond As Nullable(Of Double)
        Private ReadOnly _pathEfficiency As Nullable(Of Double)

        Public Sub New(motionEventCount As Integer,
                       medianIntervalMs As Nullable(Of Double),
                       p95IntervalMs As Nullable(Of Double),
                       medianFrequencyHz As Nullable(Of Double),
                       peakVelocityCmPerSecond As Nullable(Of Double),
                       pathEfficiency As Nullable(Of Double))
            _motionEventCount = Math.Max(0, motionEventCount)
            _medianIntervalMs = medianIntervalMs
            _p95IntervalMs = p95IntervalMs
            _medianFrequencyHz = medianFrequencyHz
            _peakVelocityCmPerSecond = peakVelocityCmPerSecond
            _pathEfficiency = pathEfficiency
        End Sub

        Public ReadOnly Property MotionEventCount As Integer
            Get
                Return _motionEventCount
            End Get
        End Property

        Public ReadOnly Property MedianIntervalMs As Nullable(Of Double)
            Get
                Return _medianIntervalMs
            End Get
        End Property

        Public ReadOnly Property P95IntervalMs As Nullable(Of Double)
            Get
                Return _p95IntervalMs
            End Get
        End Property

        Public ReadOnly Property MedianFrequencyHz As Nullable(Of Double)
            Get
                Return _medianFrequencyHz
            End Get
        End Property

        Public ReadOnly Property PeakVelocityCmPerSecond As Nullable(Of Double)
            Get
                Return _peakVelocityCmPerSecond
            End Get
        End Property

        Public ReadOnly Property PathEfficiency As Nullable(Of Double)
            Get
                Return _pathEfficiency
            End Get
        End Property
    End Class

    Public Class MousePerformanceSnapshot
        Private ReadOnly _status As MousePerformanceSessionStatus
        Private ReadOnly _isLocked As Boolean
        Private ReadOnly _isFinalized As Boolean
        Private ReadOnly _canContinue As Boolean
        Private ReadOnly _sessionDeviceId As String
        Private ReadOnly _effectiveCpi As Nullable(Of Double)
        Private ReadOnly _canComputeVelocity As Boolean
        Private ReadOnly _summary As MousePerformanceSummary
        Private ReadOnly _events As IReadOnlyList(Of MousePerformanceEvent)
        Private ReadOnly _sessionRevision As Integer
        Private ReadOnly _eventCount As Integer
        Private ReadOnly _dataQuality As MousePerformanceDataQuality
        Private ReadOnly _diagnostics As MousePerformanceDiagnostics

        Public Sub New(status As MousePerformanceSessionStatus,
                       isLocked As Boolean,
                       isFinalized As Boolean,
                       canContinue As Boolean,
                       sessionDeviceId As String,
                       effectiveCpi As Nullable(Of Double),
                       canComputeVelocity As Boolean,
                       summary As MousePerformanceSummary,
                       events As IReadOnlyList(Of MousePerformanceEvent),
                       sessionRevision As Integer,
                       eventCount As Integer,
                       dataQuality As MousePerformanceDataQuality,
                       diagnostics As MousePerformanceDiagnostics)
            _status = status
            _isLocked = isLocked
            _isFinalized = isFinalized
            _canContinue = canContinue
            _sessionDeviceId = If(sessionDeviceId, String.Empty)
            _effectiveCpi = effectiveCpi
            _canComputeVelocity = canComputeVelocity
            _summary = If(summary,
                          New MousePerformanceSummary(0, 0L, 0L, 0.0, Nothing, Nothing, Nothing, Nothing))
            _events = If(events, Array.Empty(Of MousePerformanceEvent)())
            _sessionRevision = Math.Max(0, sessionRevision)
            _eventCount = Math.Max(0, eventCount)
            _dataQuality = If(dataQuality,
                              New MousePerformanceDataQuality(0, 0, 0, 0, 0, MousePerformanceDataQualityLevel.None))
            _diagnostics = If(diagnostics,
                              New MousePerformanceDiagnostics(0, Nothing, Nothing, Nothing, Nothing, Nothing))
        End Sub

        Public ReadOnly Property Status As MousePerformanceSessionStatus
            Get
                Return _status
            End Get
        End Property

        Public ReadOnly Property IsLocked As Boolean
            Get
                Return _isLocked
            End Get
        End Property

        Public ReadOnly Property IsFinalized As Boolean
            Get
                Return _isFinalized
            End Get
        End Property

        Public ReadOnly Property CanContinue As Boolean
            Get
                Return _canContinue
            End Get
        End Property

        Public ReadOnly Property SessionDeviceId As String
            Get
                Return _sessionDeviceId
            End Get
        End Property

        Public ReadOnly Property EffectiveCpi As Nullable(Of Double)
            Get
                Return _effectiveCpi
            End Get
        End Property

        Public ReadOnly Property CanComputeVelocity As Boolean
            Get
                Return _canComputeVelocity
            End Get
        End Property

        Public ReadOnly Property Summary As MousePerformanceSummary
            Get
                Return _summary
            End Get
        End Property

        Public ReadOnly Property Events As IReadOnlyList(Of MousePerformanceEvent)
            Get
                Return _events
            End Get
        End Property

        Public ReadOnly Property SessionRevision As Integer
            Get
                Return _sessionRevision
            End Get
        End Property

        Public ReadOnly Property EventCount As Integer
            Get
                Return _eventCount
            End Get
        End Property

        Public ReadOnly Property DataQuality As MousePerformanceDataQuality
            Get
                Return _dataQuality
            End Get
        End Property

        Public ReadOnly Property Diagnostics As MousePerformanceDiagnostics
            Get
                Return _diagnostics
            End Get
        End Property

        Public ReadOnly Property HasData As Boolean
            Get
                Return _eventCount > 0
            End Get
        End Property
    End Class
End Namespace
