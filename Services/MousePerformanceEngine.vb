Imports WpfApp1.Models

Namespace Services
    Public Class MousePerformanceEngine
        Private Const WheelButtonMask As UShort = NativeMethods.RI_MOUSE_WHEEL Or NativeMethods.RI_MOUSE_HWHEEL
        Private Const DiagnosticsRefreshIntervalMs As Double = 500.0

        Private ReadOnly _events As New MousePerformanceEventBuffer()
        Private ReadOnly _positiveIntervalsMs As New List(Of Double)()
        Private ReadOnly _analysisOptions As MousePerformanceAnalysisOptions

        Private _isCollecting As Boolean
        Private _isFinalized As Boolean
        Private _sessionDeviceId As String
        Private _effectiveCpi As Nullable(Of Double)
        Private _canComputeVelocity As Boolean
        Private _segmentRawStartTicks As Long
        Private _segmentLogicalStartTicks As Long
        Private _segmentHasAnchor As Boolean
        Private _lastLogicalTicks As Long
        Private _lastAcceptedRawTicks As Long
        Private _hasAcceptedRawTicks As Boolean
        Private _sessionRevision As Integer
        Private _summaryEventCount As Integer
        Private _summarySumX As Long
        Private _summarySumY As Long
        Private _summaryPathCounts As Double
        Private _droppedPacketCount As Integer
        Private _filteredButtonOnlyCount As Integer
        Private _filteredWheelOnlyCount As Integer
        Private _outOfOrderTimestampCount As Integer
        Private _zeroIntervalCount As Integer
        Private _maxRawVelocityCountsPerMs As Double
        Private _cachedDiagnostics As MousePerformanceDiagnostics
        Private _cachedDiagnosticsRevision As Integer
        Private _cachedDiagnosticsComputedAtTicks As Long
        Private _cachedDiagnosticsCpi As Nullable(Of Double)
        Private _cachedDiagnosticsCanComputeVelocity As Boolean

        Public Sub New(Optional analysisOptions As MousePerformanceAnalysisOptions = Nothing)
            _analysisOptions = If(analysisOptions, MousePerformanceAnalysisOptions.Default)
            _effectiveCpi = 800.0
            _canComputeVelocity = True
            ResetSession()
        End Sub

        Public ReadOnly Property AnalysisOptions As MousePerformanceAnalysisOptions
            Get
                Return _analysisOptions
            End Get
        End Property

        Public Sub ResetSession()
            _events.Clear()
            _positiveIntervalsMs.Clear()
            _isCollecting = False
            _isFinalized = False
            _sessionDeviceId = String.Empty
            _segmentRawStartTicks = 0L
            _segmentLogicalStartTicks = 0L
            _segmentHasAnchor = False
            _lastLogicalTicks = 0L
            _lastAcceptedRawTicks = 0L
            _hasAcceptedRawTicks = False
            _sessionRevision = 0
            _summaryEventCount = 0
            _summarySumX = 0L
            _summarySumY = 0L
            _summaryPathCounts = 0.0
            _droppedPacketCount = 0
            _filteredButtonOnlyCount = 0
            _filteredWheelOnlyCount = 0
            _outOfOrderTimestampCount = 0
            _zeroIntervalCount = 0
            _maxRawVelocityCountsPerMs = 0.0
            InvalidateDiagnosticsCache()
        End Sub

        Public Sub SetCpiState(effectiveCpi As Nullable(Of Double), canComputeVelocity As Boolean)
            _effectiveCpi = effectiveCpi
            _canComputeVelocity = canComputeVelocity AndAlso effectiveCpi.HasValue AndAlso effectiveCpi.Value > 0.0
            InvalidateDiagnosticsCache()
        End Sub

        Public Sub BeginCollecting(deviceId As String, startedAtTicks As Long, startFresh As Boolean)
            If startFresh Then
                ResetSession()
            End If

            _sessionDeviceId = If(deviceId, String.Empty)
            _isCollecting = True
            _isFinalized = False
            _segmentRawStartTicks = Math.Max(0L, startedAtTicks)
            _segmentLogicalStartTicks = If(_summaryEventCount = 0, 0L, _lastLogicalTicks)
            _segmentHasAnchor = _summaryEventCount > 0
        End Sub

        Public Sub PauseCollecting()
            _isCollecting = False
            _segmentHasAnchor = False
        End Sub

        Public Sub StopCollecting()
            _isCollecting = False
            _segmentHasAnchor = False
            _isFinalized = _summaryEventCount > 0
        End Sub

        Public Sub ReportDroppedPackets(count As Integer)
            If count <= 0 Then
                Return
            End If

            _droppedPacketCount += count
            AdvanceRevision()
            InvalidateDiagnosticsCache()
        End Sub

        Public Sub PushPacket(packet As RawMousePacket)
            If packet Is Nothing OrElse Not _isCollecting Then
                Return
            End If

            Dim packetKind = ClassifyPacket(packet)
            If ShouldFilterPacket(packetKind) Then
                RecordFilteredPacket(packetKind)
                Return
            End If

            Dim logicalTicks = ResolveLogicalTicks(packet.TimestampTicks)
            If _summaryEventCount > 0 Then
                If packet.TimestampTicks < _lastAcceptedRawTicks Then
                    _outOfOrderTimestampCount += 1
                End If

                Dim intervalTicks = logicalTicks - _lastLogicalTicks
                If intervalTicks <= 0L Then
                    _zeroIntervalCount += 1
                Else
                    Dim intervalMs = TicksToMilliseconds(intervalTicks)
                    _positiveIntervalsMs.Add(intervalMs)
                    Dim rawSpeed = Math.Sqrt((CDbl(packet.DeltaX) * packet.DeltaX) + (CDbl(packet.DeltaY) * packet.DeltaY)) / intervalMs
                    If rawSpeed > _maxRawVelocityCountsPerMs Then
                        _maxRawVelocityCountsPerMs = rawSpeed
                    End If
                End If
            End If

            _summaryEventCount += 1
            _summarySumX += packet.DeltaX
            _summarySumY += packet.DeltaY
            _summaryPathCounts += Math.Sqrt((CDbl(packet.DeltaX) * packet.DeltaX) + (CDbl(packet.DeltaY) * packet.DeltaY))

            _events.Add(New MousePerformanceEvent(packet.DeltaX,
                                                  packet.DeltaY,
                                                  packet.ButtonFlags,
                                                  packet.TimestampTicks,
                                                  logicalTicks,
                                                  packetKind,
                                                  _summarySumX,
                                                  _summarySumY))
            _lastLogicalTicks = logicalTicks
            _lastAcceptedRawTicks = packet.TimestampTicks
            _hasAcceptedRawTicks = True
            AdvanceRevision()
            InvalidateDiagnosticsCache()
        End Sub

        Public ReadOnly Property IsCollecting As Boolean
            Get
                Return _isCollecting
            End Get
        End Property

        Public ReadOnly Property IsFinalized As Boolean
            Get
                Return _isFinalized
            End Get
        End Property

        Public ReadOnly Property SessionDeviceId As String
            Get
                Return _sessionDeviceId
            End Get
        End Property

        Public ReadOnly Property HasData As Boolean
            Get
                Return _summaryEventCount > 0
            End Get
        End Property

        Public ReadOnly Property CanContinue As Boolean
            Get
                Return _summaryEventCount > 0 AndAlso Not _isCollecting AndAlso Not _isFinalized
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

        Public Function CreateSnapshot(status As MousePerformanceSessionStatus,
                                       isLocked As Boolean,
                                       includeEvents As Boolean) As MousePerformanceSnapshot
            Dim summary = CreateSummary()
            Dim events = If(includeEvents,
                            _events.CreateReadOnlyView(_events.Count),
                            Array.Empty(Of MousePerformanceEvent)())
            Return New MousePerformanceSnapshot(status,
                                                isLocked,
                                                _isFinalized,
                                                CanContinue,
                                                _sessionDeviceId,
                                                _effectiveCpi,
                                                _canComputeVelocity,
                                                summary,
                                                events,
                                                _sessionRevision,
                                                _events.Count,
                                                CreateDataQuality(),
                                                CreateDiagnostics(includeEvents))
        End Function

        Public Function CreateSummary() As MousePerformanceSummary
            Dim distanceCpi = If(_effectiveCpi.HasValue AndAlso _effectiveCpi.Value > 0.0, _effectiveCpi, CType(Nothing, Nullable(Of Double)))
            Dim sumXCm As Nullable(Of Double) = Nothing
            Dim sumYCm As Nullable(Of Double) = Nothing
            Dim pathCm As Nullable(Of Double) = Nothing

            If distanceCpi.HasValue Then
                sumXCm = Math.Abs(_summarySumX / distanceCpi.Value * 2.54)
                sumYCm = Math.Abs(_summarySumY / distanceCpi.Value * 2.54)
                pathCm = _summaryPathCounts / distanceCpi.Value * 2.54
            End If

            Return New MousePerformanceSummary(_summaryEventCount,
                                               _summarySumX,
                                               _summarySumY,
                                               _summaryPathCounts,
                                               distanceCpi,
                                               sumXCm,
                                               sumYCm,
                                               pathCm)
        End Function

        Private Function CreateDataQuality() As MousePerformanceDataQuality
            Dim qualityLevel = MousePerformanceDataQualityLevel.None
            If _summaryEventCount > 0 Then
                qualityLevel = If(_droppedPacketCount > 0 OrElse
                                  _outOfOrderTimestampCount > 0 OrElse
                                  _zeroIntervalCount > 0,
                                  MousePerformanceDataQualityLevel.Degraded,
                                  MousePerformanceDataQualityLevel.Good)
            ElseIf _droppedPacketCount > 0 OrElse _outOfOrderTimestampCount > 0 OrElse _zeroIntervalCount > 0 Then
                qualityLevel = MousePerformanceDataQualityLevel.Degraded
            End If

            Return New MousePerformanceDataQuality(_droppedPacketCount,
                                                   _filteredButtonOnlyCount,
                                                   _filteredWheelOnlyCount,
                                                   _outOfOrderTimestampCount,
                                                   _zeroIntervalCount,
                                                   qualityLevel)
        End Function

        Private Function CreateDiagnostics(includeDetailed As Boolean) As MousePerformanceDiagnostics
            If Not includeDetailed Then
                Return CreateQuickDiagnostics()
            End If

            Dim nowTicks = Stopwatch.GetTimestamp()
            If _cachedDiagnostics IsNot Nothing AndAlso
               _cachedDiagnosticsRevision = _sessionRevision AndAlso
               Nullable.Equals(_cachedDiagnosticsCpi, _effectiveCpi) AndAlso
               _cachedDiagnosticsCanComputeVelocity = _canComputeVelocity AndAlso
               (Not _isCollecting OrElse TicksToMilliseconds(nowTicks - _cachedDiagnosticsComputedAtTicks) < DiagnosticsRefreshIntervalMs) Then
                Return _cachedDiagnostics
            End If

            Dim medianIntervalMs As Nullable(Of Double) = Nothing
            Dim p95IntervalMs As Nullable(Of Double) = Nothing
            Dim medianFrequencyHz As Nullable(Of Double) = Nothing

            If _positiveIntervalsMs.Count > 0 Then
                Dim orderedIntervals = _positiveIntervalsMs.ToArray()
                Array.Sort(orderedIntervals)
                medianIntervalMs = ResolvePercentile(orderedIntervals, 0.5)
                p95IntervalMs = ResolvePercentile(orderedIntervals, 0.95)
                If medianIntervalMs.HasValue AndAlso medianIntervalMs.Value > 0.0 Then
                    medianFrequencyHz = 1000.0 / medianIntervalMs.Value
                End If
            End If

            _cachedDiagnostics = New MousePerformanceDiagnostics(_summaryEventCount,
                                                                 medianIntervalMs,
                                                                 p95IntervalMs,
                                                                 medianFrequencyHz,
                                                                 ResolvePeakVelocity(),
                                                                 ResolvePathEfficiency())
            _cachedDiagnosticsRevision = _sessionRevision
            _cachedDiagnosticsComputedAtTicks = nowTicks
            _cachedDiagnosticsCpi = _effectiveCpi
            _cachedDiagnosticsCanComputeVelocity = _canComputeVelocity
            Return _cachedDiagnostics
        End Function

        Private Function CreateQuickDiagnostics() As MousePerformanceDiagnostics
            If _cachedDiagnostics IsNot Nothing AndAlso
               _cachedDiagnosticsRevision = _sessionRevision AndAlso
               Nullable.Equals(_cachedDiagnosticsCpi, _effectiveCpi) AndAlso
               _cachedDiagnosticsCanComputeVelocity = _canComputeVelocity Then
                Return _cachedDiagnostics
            End If

            Return New MousePerformanceDiagnostics(_summaryEventCount,
                                                   Nothing,
                                                   Nothing,
                                                   Nothing,
                                                   ResolvePeakVelocity(),
                                                   ResolvePathEfficiency())
        End Function

        Private Function ResolvePeakVelocity() As Nullable(Of Double)
            If Not _canComputeVelocity OrElse Not _effectiveCpi.HasValue OrElse _effectiveCpi.Value <= 0.0 Then
                Return Nothing
            End If

            Return _maxRawVelocityCountsPerMs / _effectiveCpi.Value * 25.4
        End Function

        Private Function ResolvePathEfficiency() As Nullable(Of Double)
            If _summaryEventCount <= 0 OrElse _summaryPathCounts <= 0.0 Then
                Return Nothing
            End If

            Dim net = Math.Sqrt((CDbl(_summarySumX) * _summarySumX) + (CDbl(_summarySumY) * _summarySumY))
            If net <= 0.0 Then
                Return 0.0
            End If

            Return Math.Min(1.0, Math.Max(0.0, net / _summaryPathCounts))
        End Function

        Private Function ResolveLogicalTicks(rawTicks As Long) As Long
            If Not _segmentHasAnchor Then
                If _summaryEventCount = 0 Then
                    _segmentRawStartTicks = rawTicks
                    _segmentLogicalStartTicks = 0L
                End If

                _segmentHasAnchor = True
            End If

            Dim logicalTicks = _segmentLogicalStartTicks + Math.Max(0L, rawTicks - _segmentRawStartTicks)
            If _summaryEventCount > 0 AndAlso logicalTicks < _lastLogicalTicks Then
                logicalTicks = _lastLogicalTicks
            End If

            Return logicalTicks
        End Function

        Private Shared Function ClassifyPacket(packet As RawMousePacket) As MousePerformancePacketKind
            Dim hasMotion = packet.DeltaX <> 0 OrElse packet.DeltaY <> 0
            Dim hasWheel = (packet.ButtonFlags And WheelButtonMask) <> 0US
            Dim hasButton = (packet.ButtonFlags And Not WheelButtonMask) <> 0US

            If hasMotion Then
                If hasWheel Then
                    Return MousePerformancePacketKind.MotionWithWheel
                End If

                If hasButton Then
                    Return MousePerformancePacketKind.MotionWithButton
                End If

                Return MousePerformancePacketKind.Motion
            End If

            If hasWheel Then
                Return MousePerformancePacketKind.WheelOnly
            End If

            Return MousePerformancePacketKind.ButtonOnly
        End Function

        Private Function ShouldFilterPacket(packetKind As MousePerformancePacketKind) As Boolean
            Select Case packetKind
                Case MousePerformancePacketKind.ButtonOnly
                    Return _analysisOptions.ExcludeButtonOnly
                Case MousePerformancePacketKind.WheelOnly
                    Return _analysisOptions.ExcludeWheelOnly
                Case Else
                    Return False
            End Select
        End Function

        Private Sub RecordFilteredPacket(packetKind As MousePerformancePacketKind)
            Select Case packetKind
                Case MousePerformancePacketKind.ButtonOnly
                    _filteredButtonOnlyCount += 1
                Case MousePerformancePacketKind.WheelOnly
                    _filteredWheelOnlyCount += 1
            End Select

            AdvanceRevision()
            InvalidateDiagnosticsCache()
        End Sub

        Private Sub AdvanceRevision()
            If _sessionRevision = Integer.MaxValue Then
                _sessionRevision = 1
            Else
                _sessionRevision += 1
            End If
        End Sub

        Private Sub InvalidateDiagnosticsCache()
            _cachedDiagnostics = Nothing
            _cachedDiagnosticsRevision = -1
            _cachedDiagnosticsComputedAtTicks = 0L
            _cachedDiagnosticsCpi = Nothing
            _cachedDiagnosticsCanComputeVelocity = False
        End Sub

        Public Shared Function CreateChartRenderFrame(snapshot As MousePerformanceSnapshot,
                                                      plotType As MousePerformancePlotType,
                                                      startIndex As Integer,
                                                      endIndex As Integer,
                                                      showStem As Boolean,
                                                      showLines As Boolean,
                                                      Optional analysisOptions As MousePerformanceAnalysisOptions = Nothing) As MousePerformanceChartRenderFrame
            Dim options = If(analysisOptions, MousePerformanceAnalysisOptions.Default)
            Dim events = If(snapshot?.Events, Array.Empty(Of MousePerformanceEvent)())

            If events.Count = 0 Then
                Return CreateUnavailableFrame(plotType, showStem, showLines)
            End If

            Dim clampedStart = Math.Max(0, Math.Min(startIndex, events.Count - 1))
            Dim clampedEnd = Math.Max(clampedStart, Math.Min(endIndex, events.Count - 1))

            If IsVelocityPlot(plotType) AndAlso (snapshot Is Nothing OrElse Not snapshot.CanComputeVelocity) Then
                Return CreateUnavailableFrame(plotType, showStem, showLines, clampedStart, clampedEnd)
            End If

            Dim scatterPrimary As New List(Of MousePerformanceChartPoint)()
            Dim scatterSecondary As New List(Of MousePerformanceChartPoint)()
            Dim rawLinePrimary As New List(Of MousePerformanceChartPoint)()
            Dim rawLineSecondary As New List(Of MousePerformanceChartPoint)()
            Dim trendPrimary As New List(Of MousePerformanceChartPoint)()
            Dim trendSecondary As New List(Of MousePerformanceChartPoint)()
            Dim stemPrimary As New List(Of MousePerformanceChartPoint)()
            Dim stemSecondary As New List(Of MousePerformanceChartPoint)()

            Dim xMin = Double.MaxValue
            Dim xMax = Double.MinValue
            Dim yMin = Double.MaxValue
            Dim yMax = Double.MinValue

            Select Case plotType
                Case MousePerformancePlotType.XCountVsTime
                    BuildSingleAxisCountSeries(events, clampedStart, clampedEnd, True, scatterPrimary, rawLinePrimary, stemPrimary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildMovingAverageTrend(rawLinePrimary, options))
                Case MousePerformancePlotType.YCountVsTime
                    BuildSingleAxisCountSeries(events, clampedStart, clampedEnd, False, scatterPrimary, rawLinePrimary, stemPrimary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildMovingAverageTrend(rawLinePrimary, options))
                Case MousePerformancePlotType.XYCountVsTime
                    BuildDualAxisCountSeries(events, clampedStart, clampedEnd, scatterPrimary, scatterSecondary, rawLinePrimary, rawLineSecondary, stemPrimary, stemSecondary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildMovingAverageTrend(rawLinePrimary, options))
                    trendSecondary.AddRange(BuildMovingAverageTrend(rawLineSecondary, options))
                Case MousePerformancePlotType.IntervalVsTime
                    Dim intervalPoints = BuildIntervalSamples(events, clampedStart, clampedEnd)
                    AppendSeriesPoints(intervalPoints, scatterPrimary, rawLinePrimary, stemPrimary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildMovingAverageTrend(rawLinePrimary, options))
                Case MousePerformancePlotType.FrequencyVsTime
                    Dim frequencySamples = BuildFrequencySamples(events, clampedStart, clampedEnd)
                    AppendSeriesPoints(frequencySamples.RawPoints, scatterPrimary, rawLinePrimary, stemPrimary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildFrequencyTrend(frequencySamples, options))
                Case MousePerformancePlotType.XVelocityVsTime
                    Dim xVelocitySamples = BuildVelocitySamples(events, clampedStart, clampedEnd, snapshot.EffectiveCpi, True)
                    AppendSeriesPoints(xVelocitySamples.RawPoints, scatterPrimary, rawLinePrimary, stemPrimary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildVelocityTrend(events, clampedStart, clampedEnd, snapshot.EffectiveCpi, True, options))
                Case MousePerformancePlotType.YVelocityVsTime
                    Dim yVelocitySamples = BuildVelocitySamples(events, clampedStart, clampedEnd, snapshot.EffectiveCpi, False)
                    AppendSeriesPoints(yVelocitySamples.RawPoints, scatterPrimary, rawLinePrimary, stemPrimary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildVelocityTrend(events, clampedStart, clampedEnd, snapshot.EffectiveCpi, False, options))
                Case MousePerformancePlotType.XYVelocityVsTime
                    Dim xSamples = BuildVelocitySamples(events, clampedStart, clampedEnd, snapshot.EffectiveCpi, True)
                    Dim ySamples = BuildVelocitySamples(events, clampedStart, clampedEnd, snapshot.EffectiveCpi, False)
                    AppendSeriesPoints(xSamples.RawPoints, scatterPrimary, rawLinePrimary, stemPrimary, xMin, xMax, yMin, yMax)
                    AppendSeriesPoints(ySamples.RawPoints, scatterSecondary, rawLineSecondary, stemSecondary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildVelocityTrend(events, clampedStart, clampedEnd, snapshot.EffectiveCpi, True, options))
                    trendSecondary.AddRange(BuildVelocityTrend(events, clampedStart, clampedEnd, snapshot.EffectiveCpi, False, options))
                Case MousePerformancePlotType.XSumVsTime
                    BuildSingleAxisSumSeries(events, clampedStart, clampedEnd, True, scatterPrimary, rawLinePrimary, stemPrimary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildMovingAverageTrend(rawLinePrimary, options))
                Case MousePerformancePlotType.YSumVsTime
                    BuildSingleAxisSumSeries(events, clampedStart, clampedEnd, False, scatterPrimary, rawLinePrimary, stemPrimary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildMovingAverageTrend(rawLinePrimary, options))
                Case MousePerformancePlotType.XYSumVsTime
                    BuildDualAxisSumSeries(events, clampedStart, clampedEnd, scatterPrimary, scatterSecondary, rawLinePrimary, rawLineSecondary, stemPrimary, stemSecondary, xMin, xMax, yMin, yMax)
                    trendPrimary.AddRange(BuildMovingAverageTrend(rawLinePrimary, options))
                    trendSecondary.AddRange(BuildMovingAverageTrend(rawLineSecondary, options))
                Case Else
                    BuildTrajectorySeries(events, clampedStart, clampedEnd, scatterPrimary, rawLinePrimary, xMin, xMax, yMin, yMax)
            End Select

            If scatterPrimary.Count = 0 AndAlso scatterSecondary.Count = 0 AndAlso rawLinePrimary.Count <= 1 AndAlso rawLineSecondary.Count <= 1 Then
                Return CreateUnavailableFrame(plotType, showStem, showLines, clampedStart, clampedEnd)
            End If

            Dim series As New List(Of MousePerformanceChartSeries)()

            If plotType = MousePerformancePlotType.XVsY Then
                If rawLinePrimary.Count > 1 Then
                    series.Add(New MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line,
                                                               MousePerformanceChartSeriesPalette.Accent,
                                                               rawLinePrimary.ToArray()))
                End If
            Else
                Dim lineSourcePrimary As IReadOnlyList(Of MousePerformanceChartPoint) = If(showLines, rawLinePrimary, trendPrimary)
                Dim lineSourceSecondary As IReadOnlyList(Of MousePerformanceChartPoint) = If(showLines, rawLineSecondary, trendSecondary)

                If lineSourcePrimary.Count > 1 Then
                    series.Add(New MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line,
                                                               If(showLines, MousePerformanceChartSeriesPalette.Accent, MousePerformanceChartSeriesPalette.Primary),
                                                               lineSourcePrimary.ToArray()))
                End If

                If lineSourceSecondary.Count > 1 Then
                    series.Add(New MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Line,
                                                               MousePerformanceChartSeriesPalette.Secondary,
                                                               lineSourceSecondary.ToArray()))
                End If
            End If

            If showStem AndAlso plotType <> MousePerformancePlotType.XVsY Then
                If stemPrimary.Count > 0 Then
                    series.Add(New MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Stem,
                                                               MousePerformanceChartSeriesPalette.Primary,
                                                               stemPrimary.ToArray()))
                End If

                If stemSecondary.Count > 0 Then
                    series.Add(New MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Stem,
                                                               MousePerformanceChartSeriesPalette.Secondary,
                                                               stemSecondary.ToArray()))
                End If
            End If

            If scatterPrimary.Count > 0 Then
                series.Add(New MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Scatter,
                                                           MousePerformanceChartSeriesPalette.Primary,
                                                           scatterPrimary.ToArray()))
            End If

            If scatterSecondary.Count > 0 Then
                series.Add(New MousePerformanceChartSeries(MousePerformanceChartSeriesKind.Scatter,
                                                           MousePerformanceChartSeriesPalette.Secondary,
                                                           scatterSecondary.ToArray()))
            End If

            ExpandAxisRange(xMin, xMax)
            ExpandYAxisRange(plotType, yMin, yMax)

            Return New MousePerformanceChartRenderFrame(plotType,
                                                        String.Empty,
                                                        String.Empty,
                                                        String.Empty,
                                                        String.Empty,
                                                        String.Empty,
                                                        True,
                                                        String.Empty,
                                                        clampedStart,
                                                        clampedEnd,
                                                        showStem,
                                                        showLines,
                                                        xMin,
                                                        xMax,
                                                        yMin,
                                                        yMax,
                                                        series.ToArray())
        End Function

        Private Shared Sub BuildSingleAxisCountSeries(events As IReadOnlyList(Of MousePerformanceEvent),
                                                      startIndex As Integer,
                                                      endIndex As Integer,
                                                      isXAxis As Boolean,
                                                      scatter As ICollection(Of MousePerformanceChartPoint),
                                                      rawLine As ICollection(Of MousePerformanceChartPoint),
                                                      stems As ICollection(Of MousePerformanceChartPoint),
                                                      ByRef xMin As Double,
                                                      ByRef xMax As Double,
                                                      ByRef yMin As Double,
                                                      ByRef yMax As Double)
            For index = startIndex To endIndex
                Dim value = If(isXAxis, events(index).DeltaX, events(index).DeltaY)
                AppendPoint(scatter,
                            rawLine,
                            stems,
                            New MousePerformanceChartPoint(events(index).TimestampMs, value),
                            xMin,
                            xMax,
                            yMin,
                            yMax)
            Next
        End Sub

        Private Shared Sub BuildDualAxisCountSeries(events As IReadOnlyList(Of MousePerformanceEvent),
                                                    startIndex As Integer,
                                                    endIndex As Integer,
                                                    scatterPrimary As ICollection(Of MousePerformanceChartPoint),
                                                    scatterSecondary As ICollection(Of MousePerformanceChartPoint),
                                                    rawLinePrimary As ICollection(Of MousePerformanceChartPoint),
                                                    rawLineSecondary As ICollection(Of MousePerformanceChartPoint),
                                                    stemsPrimary As ICollection(Of MousePerformanceChartPoint),
                                                    stemsSecondary As ICollection(Of MousePerformanceChartPoint),
                                                    ByRef xMin As Double,
                                                    ByRef xMax As Double,
                                                    ByRef yMin As Double,
                                                    ByRef yMax As Double)
            For index = startIndex To endIndex
                AppendPoint(scatterPrimary,
                            rawLinePrimary,
                            stemsPrimary,
                            New MousePerformanceChartPoint(events(index).TimestampMs, events(index).DeltaX),
                            xMin,
                            xMax,
                            yMin,
                            yMax)
                AppendPoint(scatterSecondary,
                            rawLineSecondary,
                            stemsSecondary,
                            New MousePerformanceChartPoint(events(index).TimestampMs, events(index).DeltaY),
                            xMin,
                            xMax,
                            yMin,
                            yMax)
            Next
        End Sub

        Private Shared Sub BuildSingleAxisSumSeries(events As IReadOnlyList(Of MousePerformanceEvent),
                                                    startIndex As Integer,
                                                    endIndex As Integer,
                                                    isXAxis As Boolean,
                                                    scatter As ICollection(Of MousePerformanceChartPoint),
                                                    rawLine As ICollection(Of MousePerformanceChartPoint),
                                                    stems As ICollection(Of MousePerformanceChartPoint),
                                                    ByRef xMin As Double,
                                                    ByRef xMax As Double,
                                                    ByRef yMin As Double,
                                                    ByRef yMax As Double)
            For index = startIndex To endIndex
                Dim value = If(isXAxis, events(index).SessionCumulativeX, events(index).SessionCumulativeY)
                AppendPoint(scatter,
                            rawLine,
                            stems,
                            New MousePerformanceChartPoint(events(index).TimestampMs, value),
                            xMin,
                            xMax,
                            yMin,
                            yMax)
            Next
        End Sub

        Private Shared Sub BuildDualAxisSumSeries(events As IReadOnlyList(Of MousePerformanceEvent),
                                                  startIndex As Integer,
                                                  endIndex As Integer,
                                                  scatterPrimary As ICollection(Of MousePerformanceChartPoint),
                                                  scatterSecondary As ICollection(Of MousePerformanceChartPoint),
                                                  rawLinePrimary As ICollection(Of MousePerformanceChartPoint),
                                                  rawLineSecondary As ICollection(Of MousePerformanceChartPoint),
                                                  stemsPrimary As ICollection(Of MousePerformanceChartPoint),
                                                  stemsSecondary As ICollection(Of MousePerformanceChartPoint),
                                                  ByRef xMin As Double,
                                                  ByRef xMax As Double,
                                                  ByRef yMin As Double,
                                                  ByRef yMax As Double)
            For index = startIndex To endIndex
                AppendPoint(scatterPrimary,
                            rawLinePrimary,
                            stemsPrimary,
                            New MousePerformanceChartPoint(events(index).TimestampMs, events(index).SessionCumulativeX),
                            xMin,
                            xMax,
                            yMin,
                            yMax)
                AppendPoint(scatterSecondary,
                            rawLineSecondary,
                            stemsSecondary,
                            New MousePerformanceChartPoint(events(index).TimestampMs, events(index).SessionCumulativeY),
                            xMin,
                            xMax,
                            yMin,
                            yMax)
            Next
        End Sub

        Private Shared Sub BuildTrajectorySeries(events As IReadOnlyList(Of MousePerformanceEvent),
                                                 startIndex As Integer,
                                                 endIndex As Integer,
                                                 scatter As ICollection(Of MousePerformanceChartPoint),
                                                 rawLine As ICollection(Of MousePerformanceChartPoint),
                                                 ByRef xMin As Double,
                                                 ByRef xMax As Double,
                                                 ByRef yMin As Double,
                                                 ByRef yMax As Double)
            Dim offsetX = If(startIndex > 0, events(startIndex - 1).SessionCumulativeX, 0L)
            Dim offsetY = If(startIndex > 0, events(startIndex - 1).SessionCumulativeY, 0L)

            For index = startIndex To endIndex
                Dim point = New MousePerformanceChartPoint(events(index).SessionCumulativeX - offsetX,
                                                           events(index).SessionCumulativeY - offsetY)
                scatter.Add(point)
                rawLine.Add(point)
                UpdateRange(point.X, point.Y, xMin, xMax, yMin, yMax)
            Next
        End Sub

        Private Shared Function BuildIntervalSamples(events As IReadOnlyList(Of MousePerformanceEvent),
                                                     startIndex As Integer,
                                                     endIndex As Integer) As List(Of MousePerformanceChartPoint)
            Dim points As New List(Of MousePerformanceChartPoint)()
            For index = Math.Max(1, startIndex) To endIndex
                Dim intervalMs = events(index).TimestampMs - events(index - 1).TimestampMs
                If intervalMs > 0.0 Then
                    points.Add(New MousePerformanceChartPoint(events(index).TimestampMs, intervalMs))
                End If
            Next

            Return points
        End Function

        Private NotInheritable Class TimeSeriesDerivedSamples
            Public Sub New(rawPoints As IReadOnlyList(Of MousePerformanceChartPoint),
                           sampleIndexes As IReadOnlyList(Of Integer),
                           sampleMeasures As IReadOnlyList(Of Double))
                Me.RawPoints = If(rawPoints, Array.Empty(Of MousePerformanceChartPoint)())
                Me.SampleIndexes = If(sampleIndexes, Array.Empty(Of Integer)())
                Me.SampleMeasures = If(sampleMeasures, Array.Empty(Of Double)())
            End Sub

            Public ReadOnly Property RawPoints As IReadOnlyList(Of MousePerformanceChartPoint)
            Public ReadOnly Property SampleIndexes As IReadOnlyList(Of Integer)
            Public ReadOnly Property SampleMeasures As IReadOnlyList(Of Double)
        End Class

        Private Shared Function BuildFrequencySamples(events As IReadOnlyList(Of MousePerformanceEvent),
                                                      startIndex As Integer,
                                                      endIndex As Integer) As TimeSeriesDerivedSamples
            Dim rawPoints As New List(Of MousePerformanceChartPoint)()
            Dim sampleIndexes As New List(Of Integer)()
            Dim sampleMeasures As New List(Of Double)()

            For index = Math.Max(1, startIndex) To endIndex
                Dim intervalMs = events(index).TimestampMs - events(index - 1).TimestampMs
                If intervalMs <= 0.0 Then
                    Continue For
                End If

                rawPoints.Add(New MousePerformanceChartPoint(events(index).TimestampMs, 1000.0 / intervalMs))
                sampleIndexes.Add(index)
                sampleMeasures.Add(intervalMs)
            Next

            Return New TimeSeriesDerivedSamples(rawPoints, sampleIndexes, sampleMeasures)
        End Function

        Private Shared Function BuildVelocitySamples(events As IReadOnlyList(Of MousePerformanceEvent),
                                                     startIndex As Integer,
                                                     endIndex As Integer,
                                                     cpi As Nullable(Of Double),
                                                     isXAxis As Boolean) As TimeSeriesDerivedSamples
            Dim rawPoints As New List(Of MousePerformanceChartPoint)()
            Dim sampleIndexes As New List(Of Integer)()
            Dim sampleMeasures As New List(Of Double)()

            If Not cpi.HasValue OrElse cpi.Value <= 0.0 Then
                Return New TimeSeriesDerivedSamples(rawPoints, sampleIndexes, sampleMeasures)
            End If

            For index = Math.Max(1, startIndex) To endIndex
                Dim intervalMs = events(index).TimestampMs - events(index - 1).TimestampMs
                If intervalMs <= 0.0 Then
                    Continue For
                End If

                Dim delta = If(isXAxis, events(index).DeltaX, events(index).DeltaY)
                rawPoints.Add(New MousePerformanceChartPoint(events(index).TimestampMs, delta / intervalMs / cpi.Value * 25.4))
                sampleIndexes.Add(index)
                sampleMeasures.Add(intervalMs)
            Next

            Return New TimeSeriesDerivedSamples(rawPoints, sampleIndexes, sampleMeasures)
        End Function

        Private Shared Sub AppendSeriesPoints(points As IEnumerable(Of MousePerformanceChartPoint),
                                              scatter As ICollection(Of MousePerformanceChartPoint),
                                              rawLine As ICollection(Of MousePerformanceChartPoint),
                                              stems As ICollection(Of MousePerformanceChartPoint),
                                              ByRef xMin As Double,
                                              ByRef xMax As Double,
                                              ByRef yMin As Double,
                                              ByRef yMax As Double)
            If points Is Nothing Then
                Return
            End If

            For Each point In points
                AppendPoint(scatter, rawLine, stems, point, xMin, xMax, yMin, yMax)
            Next
        End Sub

        Private Shared Function BuildMovingAverageTrend(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                                        analysisOptions As MousePerformanceAnalysisOptions) As IReadOnlyList(Of MousePerformanceChartPoint)
            Dim trend As New List(Of MousePerformanceChartPoint)()
            If points Is Nothing OrElse points.Count <= 1 Then
                Return trend
            End If

            Dim prefixSums(points.Count) As Double
            For index = 0 To points.Count - 1
                prefixSums(index + 1) = prefixSums(index) + points(index).Y
            Next

            Dim halfWindow = analysisOptions.TrendWindowMs / 2.0
            Dim left = 0
            Dim right = -1

            For index = 0 To points.Count - 1
                Dim centerX = points(index).X
                While left < points.Count AndAlso centerX - points(left).X > halfWindow
                    left += 1
                End While

                While right + 1 < points.Count AndAlso points(right + 1).X - centerX <= halfWindow
                    right += 1
                End While

                Dim expandedLeft = left
                Dim expandedRight = right
                ExpandSampleWindow(points, index, analysisOptions.MinimumTrendSamples, expandedLeft, expandedRight)
                Dim sampleCount = expandedRight - expandedLeft + 1
                If sampleCount <= 0 Then
                    Continue For
                End If

                Dim mean = (prefixSums(expandedRight + 1) - prefixSums(expandedLeft)) / sampleCount
                trend.Add(New MousePerformanceChartPoint(centerX, mean))
            Next

            Return trend
        End Function

        Private Shared Function BuildFrequencyTrend(samples As TimeSeriesDerivedSamples,
                                                    analysisOptions As MousePerformanceAnalysisOptions) As IReadOnlyList(Of MousePerformanceChartPoint)
            Dim trend As New List(Of MousePerformanceChartPoint)()
            If samples Is Nothing OrElse samples.RawPoints.Count <= 1 Then
                Return trend
            End If

            Dim prefixIntervals(samples.SampleMeasures.Count) As Double
            For index = 0 To samples.SampleMeasures.Count - 1
                prefixIntervals(index + 1) = prefixIntervals(index) + samples.SampleMeasures(index)
            Next

            Dim halfWindow = analysisOptions.TrendWindowMs / 2.0
            Dim left = 0
            Dim right = -1

            For index = 0 To samples.RawPoints.Count - 1
                Dim centerX = samples.RawPoints(index).X
                While left < samples.RawPoints.Count AndAlso centerX - samples.RawPoints(left).X > halfWindow
                    left += 1
                End While

                While right + 1 < samples.RawPoints.Count AndAlso samples.RawPoints(right + 1).X - centerX <= halfWindow
                    right += 1
                End While

                Dim expandedLeft = left
                Dim expandedRight = right
                ExpandSampleWindow(samples.RawPoints, index, analysisOptions.MinimumTrendSamples, expandedLeft, expandedRight)
                Dim totalIntervalMs = prefixIntervals(expandedRight + 1) - prefixIntervals(expandedLeft)
                If totalIntervalMs <= 0.0 Then
                    Continue For
                End If

                Dim intervalCount = expandedRight - expandedLeft + 1
                trend.Add(New MousePerformanceChartPoint(centerX, 1000.0 * intervalCount / totalIntervalMs))
            Next

            Return trend
        End Function

        Private Shared Function BuildVelocityTrend(events As IReadOnlyList(Of MousePerformanceEvent),
                                                   startIndex As Integer,
                                                   endIndex As Integer,
                                                   cpi As Nullable(Of Double),
                                                   isXAxis As Boolean,
                                                   analysisOptions As MousePerformanceAnalysisOptions) As IReadOnlyList(Of MousePerformanceChartPoint)
            Dim trend As New List(Of MousePerformanceChartPoint)()
            If events Is Nothing OrElse events.Count <= 1 OrElse Not cpi.HasValue OrElse cpi.Value <= 0.0 Then
                Return trend
            End If

            Dim halfWindow = analysisOptions.TrendWindowMs / 2.0
            Dim safeStart = Math.Max(1, startIndex)

            For index = safeStart To endIndex
                Dim windowStart = index
                Dim windowEnd = index
                ExpandEventWindow(events, safeStart, endIndex, index, halfWindow, analysisOptions.MinimumTrendSamples, windowStart, windowEnd)

                If windowEnd <= windowStart Then
                    Continue For
                End If

                Dim intervalMs = events(windowEnd).TimestampMs - events(windowStart).TimestampMs
                If intervalMs <= 0.0 Then
                    Continue For
                End If

                Dim delta = If(isXAxis,
                               events(windowEnd).SessionCumulativeX - events(windowStart).SessionCumulativeX,
                               events(windowEnd).SessionCumulativeY - events(windowStart).SessionCumulativeY)
                trend.Add(New MousePerformanceChartPoint(events(index).TimestampMs,
                                                         delta / intervalMs / cpi.Value * 25.4))
            Next

            Return trend
        End Function

        Private Shared Sub ExpandSampleWindow(points As IReadOnlyList(Of MousePerformanceChartPoint),
                                              centerIndex As Integer,
                                              minimumSamples As Integer,
                                              ByRef left As Integer,
                                              ByRef right As Integer)
            If points Is Nothing OrElse points.Count = 0 Then
                Return
            End If

            Dim centerX = points(centerIndex).X
            While right - left + 1 < minimumSamples AndAlso (left > 0 OrElse right < points.Count - 1)
                If left <= 0 Then
                    right += 1
                    Continue While
                End If

                If right >= points.Count - 1 Then
                    left -= 1
                    Continue While
                End If

                Dim leftDistance = Math.Abs(centerX - points(left - 1).X)
                Dim rightDistance = Math.Abs(points(right + 1).X - centerX)
                If leftDistance <= rightDistance Then
                    left -= 1
                Else
                    right += 1
                End If
            End While
        End Sub

        Private Shared Sub ExpandEventWindow(events As IReadOnlyList(Of MousePerformanceEvent),
                                             rangeStart As Integer,
                                             rangeEnd As Integer,
                                             centerIndex As Integer,
                                             halfWindowMs As Double,
                                             minimumSamples As Integer,
                                             ByRef left As Integer,
                                             ByRef right As Integer)
            Dim centerTime = events(centerIndex).TimestampMs
            left = centerIndex
            right = centerIndex

            While left > rangeStart AndAlso centerTime - events(left - 1).TimestampMs <= halfWindowMs
                left -= 1
            End While

            While right < rangeEnd AndAlso events(right + 1).TimestampMs - centerTime <= halfWindowMs
                right += 1
            End While

            While right - left + 1 < minimumSamples AndAlso (left > rangeStart OrElse right < rangeEnd)
                If left <= rangeStart Then
                    right += 1
                    Continue While
                End If

                If right >= rangeEnd Then
                    left -= 1
                    Continue While
                End If

                Dim leftDistance = Math.Abs(centerTime - events(left - 1).TimestampMs)
                Dim rightDistance = Math.Abs(events(right + 1).TimestampMs - centerTime)
                If leftDistance <= rightDistance Then
                    left -= 1
                Else
                    right += 1
                End If
            End While
        End Sub

        Private Shared Function ResolvePercentile(sortedValues As IReadOnlyList(Of Double),
                                                  percentile As Double) As Nullable(Of Double)
            If sortedValues Is Nothing OrElse sortedValues.Count = 0 Then
                Return Nothing
            End If

            Dim clampedPercentile = Math.Max(0.0, Math.Min(1.0, percentile))
            Dim position = clampedPercentile * (sortedValues.Count - 1)
            Dim lowerIndex = CInt(Math.Floor(position))
            Dim upperIndex = CInt(Math.Ceiling(position))
            If lowerIndex = upperIndex Then
                Return sortedValues(lowerIndex)
            End If

            Dim weight = position - lowerIndex
            Return sortedValues(lowerIndex) + (sortedValues(upperIndex) - sortedValues(lowerIndex)) * weight
        End Function

        Private Shared Function IsVelocityPlot(plotType As MousePerformancePlotType) As Boolean
            Return plotType = MousePerformancePlotType.XVelocityVsTime OrElse
                   plotType = MousePerformancePlotType.YVelocityVsTime OrElse
                   plotType = MousePerformancePlotType.XYVelocityVsTime
        End Function

        Private Shared Function TicksToMilliseconds(ticks As Long) As Double
            Return ticks * 1000.0 / Stopwatch.Frequency
        End Function

        Private Shared Sub AppendPoint(scatter As ICollection(Of MousePerformanceChartPoint),
                                       line As ICollection(Of MousePerformanceChartPoint),
                                       stem As ICollection(Of MousePerformanceChartPoint),
                                       point As MousePerformanceChartPoint,
                                       ByRef xMin As Double,
                                       ByRef xMax As Double,
                                       ByRef yMin As Double,
                                       ByRef yMax As Double)
            If point Is Nothing Then
                Return
            End If

            scatter.Add(point)
            line.Add(point)
            stem.Add(point)
            UpdateRange(point.X, point.Y, xMin, xMax, yMin, yMax)
        End Sub

        Private Shared Sub UpdateRange(x As Double,
                                       y As Double,
                                       ByRef xMin As Double,
                                       ByRef xMax As Double,
                                       ByRef yMin As Double,
                                       ByRef yMax As Double)
            If x < xMin Then
                xMin = x
            End If

            If x > xMax Then
                xMax = x
            End If

            If y < yMin Then
                yMin = y
            End If

            If y > yMax Then
                yMax = y
            End If
        End Sub

        Private Shared Sub ExpandAxisRange(ByRef minimum As Double, ByRef maximum As Double)
            If minimum = Double.MaxValue OrElse maximum = Double.MinValue Then
                minimum = -1.0
                maximum = 1.0
                Return
            End If

            If Math.Abs(maximum - minimum) < 0.000001 Then
                Dim pad = Math.Max(1.0, Math.Abs(maximum) * 0.05)
                minimum -= pad
                maximum += pad
                Return
            End If

            Dim padding = (maximum - minimum) / 20.0
            minimum -= padding
            maximum += padding
        End Sub

        Private Shared Sub ExpandYAxisRange(plotType As MousePerformancePlotType,
                                            ByRef minimum As Double,
                                            ByRef maximum As Double)
            If plotType = MousePerformancePlotType.XVsY Then
                ExpandAxisRange(minimum, maximum)
                Return
            End If

            If minimum = Double.MaxValue OrElse maximum = Double.MinValue Then
                minimum = -1.0
                maximum = 1.0
                Return
            End If

            If minimum >= 0.0 Then
                minimum = 0.0
                Dim padding = Math.Max(1.0, maximum - minimum) / 20.0
                maximum += padding
                Return
            End If

            If maximum <= 0.0 Then
                maximum = 0.0
                Dim padding = Math.Max(1.0, maximum - minimum) / 20.0
                minimum -= padding
                Return
            End If

            ExpandAxisRange(minimum, maximum)
        End Sub

        Private Shared Function CreateUnavailableFrame(plotType As MousePerformancePlotType,
                                                       showStem As Boolean,
                                                       showLines As Boolean,
                                                       Optional startIndex As Integer = 0,
                                                       Optional endIndex As Integer = 0) As MousePerformanceChartRenderFrame
            Return New MousePerformanceChartRenderFrame(plotType,
                                                        String.Empty,
                                                        String.Empty,
                                                        String.Empty,
                                                        String.Empty,
                                                        String.Empty,
                                                        False,
                                                        String.Empty,
                                                        startIndex,
                                                        endIndex,
                                                        showStem,
                                                        showLines,
                                                        -1.0,
                                                        1.0,
                                                        -1.0,
                                                        1.0,
                                                        Array.Empty(Of MousePerformanceChartSeries)())
        End Function
    End Class
End Namespace
