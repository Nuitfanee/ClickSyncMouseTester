Imports WpfApp1.Models

Namespace Services
    Public Class AngleCalibrationEngine
        Private Const SampleMinimumSegmentLength As Double = 1.5
        Private Const SampleMinimumHorizontalRatio As Double = 0.75
        Private Const StrokeFlipMinimumNetDx As Double = 220.0
        Private Const StrokeFlipMinimumDurationMilliseconds As Double = 90.0
        Private Const StrokeFlipConfirmationDx As Double = 12.0
        Private Const StrokeMinimumNetLength As Double = 220.0
        Private Const StrokeMinimumDurationMilliseconds As Double = 90.0
        Private Const StrokeMaximumDurationMilliseconds As Double = 1500.0
        Private Const StrokeMinimumHorizontalRatio As Double = 0.75
        Private Const StrokeMinimumStraightness As Double = 0.82
        Private Const StrokeMinimumPoints As Integer = 6
        Private Const StrokeMinimumResidualRms As Double = 2.0
        Private Const StrokeResidualScale As Double = 0.045
        Private Const StrokeWeightCap As Double = 1200.0
        Private Const SideMaximumAcceptedStrokes As Integer = 40
        Private Const SideMinimumInlierCount As Integer = 4
        Private Const SideMinimumWeight As Double = 4.0
        Private Const SideOutlierFloorDegrees As Double = 1.2
        Private Const SideOutlierMadScale As Double = 2.5
        Private Const ResultSwipeThreshold As Integer = 30
        Private Const StabilityWindowMilliseconds As Double = 1500.0
        Private Const StabilityMaximumSamples As Integer = 12
        Private Const MaximumHistoricalStrokes As Integer = 40
        Private Const MaximumSnapshotPointsPerStroke As Integer = 320
        Private Const TinyEpsilon As Double = 0.000000001

        Private NotInheritable Class StrokeBuilder
            Public Sub New(startX As Double, startY As Double, startTimestampMs As Double)
                Points = New List(Of MutableTracePoint) From {
                    New MutableTracePoint(startX, startY)
                }
                Me.StartTimestampMs = startTimestampMs
                Me.LastTimestampMs = startTimestampMs
            End Sub

            Public ReadOnly Property Points As List(Of MutableTracePoint)
            Public Property StartTimestampMs As Double
            Public Property LastTimestampMs As Double
            Public Property PathLength As Double
            Public Property NetDx As Double
            Public Property NetDy As Double
            Public Property DirectionSign As Integer
            Public Property PacketCount As Integer
        End Class

        Private Structure BufferedSegment
            Public Property DeltaX As Double
            Public Property DeltaY As Double
            Public Property TimestampMs As Double
            Public Property EndX As Double
            Public Property EndY As Double
        End Structure

        Private NotInheritable Class StrokeFitSample
            Public Sub New(angleDegrees As Double, weight As Double, timestampMs As Double)
                Me.AngleDegrees = angleDegrees
                Me.Weight = weight
                Me.TimestampMs = timestampMs
            End Sub

            Public Property AngleDegrees As Double
            Public Property Weight As Double
            Public Property TimestampMs As Double
        End Class

        Private NotInheritable Class EvaluatedStroke
            Public Sub New(directionSign As Integer, angleDegrees As Double, weight As Double, timestampMs As Double)
                Me.DirectionSign = directionSign
                Me.AngleDegrees = angleDegrees
                Me.Weight = weight
                Me.TimestampMs = timestampMs
            End Sub

            Public Property DirectionSign As Integer
            Public Property AngleDegrees As Double
            Public Property Weight As Double
            Public Property TimestampMs As Double
        End Class

        Private NotInheritable Class SideComputation
            Public Property AcceptedCount As Integer
            Public Property InlierCount As Integer
            Public Property MadDegrees As Nullable(Of Double)
            Public Property ResolvedAngleDegrees As Nullable(Of Double)
            Public Property TotalInlierWeight As Double
        End Class

        Private NotInheritable Class FitComputation
            Public Sub New(left As SideComputation, right As SideComputation, candidateAngleDegrees As Nullable(Of Double))
                Me.Left = left
                Me.Right = right
                Me.CandidateAngleDegrees = candidateAngleDegrees
            End Sub

            Public ReadOnly Property Left As SideComputation
            Public ReadOnly Property Right As SideComputation
            Public ReadOnly Property CandidateAngleDegrees As Nullable(Of Double)
        End Class

        Private NotInheritable Class QualityComputation
            Public Sub New(level As AngleCalibrationQualityLevel, reason As AngleCalibrationQualityReason, score As Integer)
                Me.Level = level
                Me.Reason = reason
                Me.Score = score
            End Sub

            Public ReadOnly Property Level As AngleCalibrationQualityLevel
            Public ReadOnly Property Reason As AngleCalibrationQualityReason
            Public ReadOnly Property Score As Integer
        End Class

        Private Structure CandidateAngleSample
            Public Property Value As Double
            Public Property TimestampMs As Double
        End Structure

        Private Structure WeightedValue
            Public Property Value As Double
            Public Property Weight As Double
        End Structure

        Private NotInheritable Class MutableTracePoint
            Public Sub New(x As Double, y As Double)
                Me.X = x
                Me.Y = y
            End Sub

            Public Property X As Double
            Public Property Y As Double
        End Class

        Private ReadOnly _historicalStrokes As New List(Of List(Of MutableTracePoint))()
        Private ReadOnly _candidateAngles As New List(Of CandidateAngleSample)()
        Private ReadOnly _leftStrokeSamples As New List(Of StrokeFitSample)()
        Private ReadOnly _rightStrokeSamples As New List(Of StrokeFitSample)()
        Private ReadOnly _pendingOppositeSegments As New List(Of BufferedSegment)()
        Private _activeStroke As StrokeBuilder
        Private _isLocked As Boolean
        Private _positionX As Double
        Private _positionY As Double
        Private _swipeCount As Integer
        Private _sampleCount As Integer
        Private _oppositeDxAccum As Double
        Private _lastAcceptedStrokeDirectionSign As Integer

        Public Sub New()
            Reset()
        End Sub

        Public Sub Reset()
            _historicalStrokes.Clear()
            _candidateAngles.Clear()
            _leftStrokeSamples.Clear()
            _rightStrokeSamples.Clear()
            _pendingOppositeSegments.Clear()
            _activeStroke = Nothing
            _isLocked = False
            _positionX = 0.0
            _positionY = 0.0
            _swipeCount = 0
            _sampleCount = 0
            _oppositeDxAccum = 0.0
            _lastAcceptedStrokeDirectionSign = 0
        End Sub

        Public Sub SetLocked(isLocked As Boolean, Optional nowMs As Double = Double.NaN)
            If Not isLocked Then
                DiscardInProgressStroke()
            End If

            _isLocked = isLocked
        End Sub

        Public Sub PushPacket(packet As RawMousePacket)
            If packet Is Nothing Then
                Return
            End If

            If packet.DeltaX = 0 AndAlso packet.DeltaY = 0 Then
                Return
            End If

            Ingest(packet.DeltaX, packet.DeltaY, packet.TimestampMs)
        End Sub

        Public Function CreateRenderFrame(nowMs As Double) As AngleCalibrationRenderFrame
            PruneCandidateAngles(nowMs)

            Dim fit = ComputeFit()
            Dim hasSessionData = HasData()
            Dim recommendedAngle As Nullable(Of Double) = Nothing
            If _swipeCount >= ResultSwipeThreshold AndAlso fit.CandidateAngleDegrees.HasValue Then
                recommendedAngle = ApplyDisplayAngle(fit.CandidateAngleDegrees.Value)
            End If

            Dim stability = ComputeStability(nowMs)
            Dim quality = ComputeQuality(hasSessionData, fit)
            Dim traceStrokes = CreateTraceSnapshots()
            Dim status = ResolveStatus(hasSessionData, recommendedAngle.HasValue)

            Return New AngleCalibrationRenderFrame(status,
                                                   _isLocked,
                                                   hasSessionData,
                                                   recommendedAngle,
                                                   _swipeCount,
                                                   _sampleCount,
                                                   stability,
                                                   traceStrokes,
                                                   quality.Level,
                                                   quality.Reason,
                                                   quality.Score)
        End Function

        Private Sub Ingest(deltaX As Double, deltaY As Double, timestampMs As Double)
            If ShouldCountSampleSegment(deltaX, deltaY) Then
                _sampleCount += 1
            End If

            Dim startX = _positionX
            Dim startY = _positionY
            _positionX += deltaX
            _positionY += deltaY

            If _activeStroke Is Nothing Then
                _activeStroke = New StrokeBuilder(startX, startY, timestampMs)
            End If

            Dim segment As New BufferedSegment With {
                .DeltaX = deltaX,
                .DeltaY = deltaY,
                .TimestampMs = timestampMs,
                .EndX = _positionX,
                .EndY = _positionY
            }

            ProcessSegment(segment)
        End Sub

        Private Sub ProcessSegment(segment As BufferedSegment)
            If _activeStroke Is Nothing Then
                _activeStroke = New StrokeBuilder(segment.EndX - segment.DeltaX, segment.EndY - segment.DeltaY, segment.TimestampMs)
            End If

            Dim sign = Math.Sign(segment.DeltaX)

            If _pendingOppositeSegments.Count > 0 Then
                If sign = 0 OrElse (_activeStroke.DirectionSign <> 0 AndAlso sign <> _activeStroke.DirectionSign) Then
                    BufferOppositeSegment(segment)
                    If CanSplitStroke(_activeStroke) AndAlso _oppositeDxAccum >= StrokeFlipConfirmationDx Then
                        CompleteActiveStrokeAndRestart()
                    End If
                    Return
                End If

                FlushPendingSegmentsIntoActiveStroke()
            End If

            If _activeStroke.DirectionSign <> 0 AndAlso sign <> 0 AndAlso sign <> _activeStroke.DirectionSign Then
                BufferOppositeSegment(segment)
                If CanSplitStroke(_activeStroke) AndAlso _oppositeDxAccum >= StrokeFlipConfirmationDx Then
                    CompleteActiveStrokeAndRestart()
                End If
                Return
            End If

            AppendSegment(_activeStroke, segment)
        End Sub

        Private Sub BufferOppositeSegment(segment As BufferedSegment)
            _pendingOppositeSegments.Add(segment)

            Dim sign = Math.Sign(segment.DeltaX)
            If _activeStroke Is Nothing OrElse _activeStroke.DirectionSign = 0 Then
                Return
            End If

            If sign <> 0 AndAlso sign <> _activeStroke.DirectionSign Then
                _oppositeDxAccum += Math.Abs(segment.DeltaX)
            End If
        End Sub

        Private Sub FlushPendingSegmentsIntoActiveStroke()
            If _activeStroke Is Nothing OrElse _pendingOppositeSegments.Count = 0 Then
                _pendingOppositeSegments.Clear()
                _oppositeDxAccum = 0.0
                Return
            End If

            For Each buffered In _pendingOppositeSegments
                AppendSegment(_activeStroke, buffered)
            Next

            _pendingOppositeSegments.Clear()
            _oppositeDxAccum = 0.0
        End Sub

        Private Sub CompleteActiveStrokeAndRestart()
            If _activeStroke Is Nothing Then
                Return
            End If

            Dim completedStroke = _activeStroke
            ArchiveHistoricalStroke(completedStroke.Points)
            AcceptCompletedStroke(completedStroke)

            Dim restartTimestampMs = If(_pendingOppositeSegments.Count > 0, _pendingOppositeSegments(0).TimestampMs, completedStroke.LastTimestampMs)
            Dim lastPoint = completedStroke.Points(completedStroke.Points.Count - 1)
            _activeStroke = New StrokeBuilder(lastPoint.X, lastPoint.Y, restartTimestampMs)

            For Each buffered In _pendingOppositeSegments
                AppendSegment(_activeStroke, buffered)
            Next

            _pendingOppositeSegments.Clear()
            _oppositeDxAccum = 0.0
        End Sub

        Private Sub AcceptCompletedStroke(stroke As StrokeBuilder)
            Dim evaluated = EvaluateStroke(stroke)
            If evaluated Is Nothing Then
                Return
            End If

            Dim targetSamples = If(evaluated.DirectionSign < 0, _leftStrokeSamples, _rightStrokeSamples)
            targetSamples.Add(New StrokeFitSample(evaluated.AngleDegrees, evaluated.Weight, evaluated.TimestampMs))
            While targetSamples.Count > SideMaximumAcceptedStrokes
                targetSamples.RemoveAt(0)
            End While

            If _lastAcceptedStrokeDirectionSign <> 0 AndAlso evaluated.DirectionSign <> _lastAcceptedStrokeDirectionSign Then
                _swipeCount += 1
            End If

            _lastAcceptedStrokeDirectionSign = evaluated.DirectionSign

            Dim fit = ComputeFit()
            If fit.CandidateAngleDegrees.HasValue Then
                _candidateAngles.Add(New CandidateAngleSample With {
                    .Value = ApplyDisplayAngle(fit.CandidateAngleDegrees.Value),
                    .TimestampMs = evaluated.TimestampMs
                })
                PruneCandidateAngles(evaluated.TimestampMs)
            End If
        End Sub

        Private Shared Sub AppendSegment(target As StrokeBuilder, segment As BufferedSegment)
            If target Is Nothing Then
                Return
            End If

            Dim segmentLength = Math.Sqrt((segment.DeltaX * segment.DeltaX) + (segment.DeltaY * segment.DeltaY))
            If Double.IsNaN(segmentLength) OrElse Double.IsInfinity(segmentLength) Then
                segmentLength = 0.0
            End If

            target.PathLength += segmentLength
            target.NetDx += segment.DeltaX
            target.NetDy += segment.DeltaY
            target.LastTimestampMs = segment.TimestampMs
            target.PacketCount += 1

            Dim sign = Math.Sign(segment.DeltaX)
            If target.DirectionSign = 0 AndAlso sign <> 0 Then
                target.DirectionSign = sign
            End If

            Dim lastPoint = target.Points(target.Points.Count - 1)
            If lastPoint.X <> segment.EndX OrElse lastPoint.Y <> segment.EndY Then
                target.Points.Add(New MutableTracePoint(segment.EndX, segment.EndY))
            End If
        End Sub

        Private Shared Function ShouldCountSampleSegment(deltaX As Double, deltaY As Double) As Boolean
            Dim segmentLength = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY))
            If Double.IsNaN(segmentLength) OrElse Double.IsInfinity(segmentLength) OrElse segmentLength < SampleMinimumSegmentLength Then
                Return False
            End If

            Dim absX = Math.Abs(deltaX)
            If absX < TinyEpsilon Then
                Return False
            End If

            Return (absX / segmentLength) >= SampleMinimumHorizontalRatio
        End Function

        Private Shared Function CanSplitStroke(stroke As StrokeBuilder) As Boolean
            If stroke Is Nothing Then
                Return False
            End If

            If Math.Abs(stroke.NetDx) < StrokeFlipMinimumNetDx Then
                Return False
            End If

            Dim durationMs = stroke.LastTimestampMs - stroke.StartTimestampMs
            If Double.IsNaN(durationMs) OrElse Double.IsInfinity(durationMs) Then
                Return False
            End If

            Return durationMs >= StrokeFlipMinimumDurationMilliseconds
        End Function

        Private Function EvaluateStroke(stroke As StrokeBuilder) As EvaluatedStroke
            If stroke Is Nothing OrElse stroke.Points Is Nothing OrElse stroke.Points.Count < StrokeMinimumPoints Then
                Return Nothing
            End If

            Dim directionSign = Math.Sign(stroke.NetDx)
            If directionSign = 0 Then
                Return Nothing
            End If

            Dim durationMs = stroke.LastTimestampMs - stroke.StartTimestampMs
            If Double.IsNaN(durationMs) OrElse
               Double.IsInfinity(durationMs) OrElse
               durationMs < StrokeMinimumDurationMilliseconds OrElse
               durationMs > StrokeMaximumDurationMilliseconds Then
                Return Nothing
            End If

            Dim netLength = Math.Sqrt((stroke.NetDx * stroke.NetDx) + (stroke.NetDy * stroke.NetDy))
            If Double.IsNaN(netLength) OrElse Double.IsInfinity(netLength) OrElse netLength < StrokeMinimumNetLength Then
                Return Nothing
            End If

            Dim absNetDx = Math.Abs(stroke.NetDx)
            If absNetDx < TinyEpsilon Then
                Return Nothing
            End If

            Dim horizontalRatio = absNetDx / netLength
            If horizontalRatio < StrokeMinimumHorizontalRatio Then
                Return Nothing
            End If

            Dim effectivePathLength = Math.Max(stroke.PathLength, netLength)
            If effectivePathLength < TinyEpsilon Then
                Return Nothing
            End If

            Dim straightness = netLength / effectivePathLength
            If straightness < StrokeMinimumStraightness Then
                Return Nothing
            End If

            Dim angleDegrees As Double
            Dim residualRms As Double
            If Not TryComputeStrokeFit(stroke.Points, stroke.NetDx, stroke.NetDy, angleDegrees, residualRms) Then
                Return Nothing
            End If

            Dim residualLimit = Math.Max(StrokeMinimumResidualRms, netLength * StrokeResidualScale)
            If residualRms > residualLimit Then
                Return Nothing
            End If

            Dim weight = Math.Min(netLength, StrokeWeightCap) * straightness
            If Double.IsNaN(weight) OrElse Double.IsInfinity(weight) OrElse weight <= 0.0 Then
                Return Nothing
            End If

            Return New EvaluatedStroke(directionSign, angleDegrees, weight, stroke.LastTimestampMs)
        End Function

        Private Shared Function TryComputeStrokeFit(points As List(Of MutableTracePoint),
                                                    netDx As Double,
                                                    netDy As Double,
                                                    ByRef angleDegrees As Double,
                                                    ByRef residualRms As Double) As Boolean
            angleDegrees = 0.0
            residualRms = 0.0

            If points Is Nothing OrElse points.Count < 2 Then
                Return False
            End If

            Dim meanX = 0.0
            Dim meanY = 0.0
            For Each point In points
                meanX += point.X
                meanY += point.Y
            Next

            meanX /= points.Count
            meanY /= points.Count

            Dim sxx = 0.0
            Dim syy = 0.0
            Dim sxy = 0.0
            For Each point In points
                Dim dx = point.X - meanX
                Dim dy = point.Y - meanY
                sxx += dx * dx
                syy += dy * dy
                sxy += dx * dy
            Next

            Dim diff = sxx - syy
            Dim root = Math.Sqrt((diff * diff) + (4.0 * sxy * sxy))
            Dim vectorX = 1.0
            Dim vectorY = 0.0

            If root > TinyEpsilon Then
                Dim lambda1 = (sxx + syy + root) / 2.0
                vectorX = sxy
                vectorY = lambda1 - sxx

                If Math.Abs(vectorX) + Math.Abs(vectorY) < TinyEpsilon Then
                    vectorX = lambda1 - syy
                    vectorY = sxy
                End If
            End If

            Dim magnitude = Math.Sqrt((vectorX * vectorX) + (vectorY * vectorY))
            If magnitude < TinyEpsilon Then
                Return False
            End If

            vectorX /= magnitude
            vectorY /= magnitude

            Dim normalX = -vectorY
            Dim normalY = vectorX
            Dim sumSquaredDistances = 0.0
            For Each point In points
                Dim dx = point.X - meanX
                Dim dy = point.Y - meanY
                Dim perpendicularDistance = (dx * normalX) + (dy * normalY)
                sumSquaredDistances += perpendicularDistance * perpendicularDistance
            Next

            residualRms = Math.Sqrt(sumSquaredDistances / points.Count)
            Dim acuteDegrees = Math.Atan2(Math.Abs(vectorY), Math.Abs(vectorX)) * 180.0 / Math.PI
            Dim sign = ComputeCorrectionSign(netDx, netDy)
            angleDegrees = Clamp(sign * acuteDegrees, -90.0, 90.0)
            Return True
        End Function

        Private Function ComputeFit() As FitComputation
            Dim left = ComputeSide(_leftStrokeSamples)
            Dim right = ComputeSide(_rightStrokeSamples)

            Dim candidate As Nullable(Of Double) = Nothing
            If left.ResolvedAngleDegrees.HasValue AndAlso right.ResolvedAngleDegrees.HasValue Then
                candidate = Clamp(Wrap180(left.ResolvedAngleDegrees.Value +
                                          (AngleDifference(right.ResolvedAngleDegrees.Value, left.ResolvedAngleDegrees.Value) / 2.0)),
                                  -90.0,
                                  90.0)
            End If

            Return New FitComputation(left, right, candidate)
        End Function

        Private Shared Function ComputeSide(samples As List(Of StrokeFitSample)) As SideComputation
            Dim result As New SideComputation With {
                .AcceptedCount = If(samples Is Nothing, 0, samples.Count),
                .InlierCount = 0,
                .MadDegrees = Nothing,
                .ResolvedAngleDegrees = Nothing,
                .TotalInlierWeight = 0.0
            }

            If samples Is Nothing OrElse samples.Count = 0 Then
                Return result
            End If

            Dim centerValues As New List(Of WeightedValue)(samples.Count)
            For Each sample In samples
                centerValues.Add(New WeightedValue With {
                    .Value = sample.AngleDegrees,
                    .Weight = Math.Max(0.0, sample.Weight)
                })
            Next

            Dim center = ComputeWeightedMedian(centerValues)
            If Not center.HasValue Then
                Return result
            End If

            Dim deviationValues As New List(Of WeightedValue)(samples.Count)
            For Each sample In samples
                deviationValues.Add(New WeightedValue With {
                    .Value = Math.Abs(AngleDifference(sample.AngleDegrees, center.Value)),
                    .Weight = Math.Max(0.0, sample.Weight)
                })
            Next

            Dim mad = ComputeWeightedMedian(deviationValues)
            If mad.HasValue Then
                result.MadDegrees = mad.Value
            End If

            Dim threshold = Math.Max(SideOutlierFloorDegrees, SideOutlierMadScale * If(mad.HasValue, mad.Value, 0.0))
            Dim inliers As New List(Of StrokeFitSample)()
            Dim inlierWeight = 0.0
            For Each sample In samples
                Dim deviation = Math.Abs(AngleDifference(sample.AngleDegrees, center.Value))
                If deviation <= threshold Then
                    inliers.Add(sample)
                    inlierWeight += Math.Max(0.0, sample.Weight)
                End If
            Next

            result.InlierCount = inliers.Count
            result.TotalInlierWeight = inlierWeight

            If inliers.Count < SideMinimumInlierCount OrElse inlierWeight < SideMinimumWeight Then
                Return result
            End If

            result.ResolvedAngleDegrees = ComputeWeightedAngleAverage(center.Value, inliers)
            Return result
        End Function

        Private Shared Function ComputeWeightedMedian(values As List(Of WeightedValue)) As Nullable(Of Double)
            If values Is Nothing OrElse values.Count = 0 Then
                Return Nothing
            End If

            Dim sorted = New List(Of WeightedValue)(values.Count)
            For Each value In values
                sorted.Add(value)
            Next

            sorted.Sort(Function(left, right) left.Value.CompareTo(right.Value))

            Dim totalWeight = 0.0
            For Each value In sorted
                totalWeight += Math.Max(0.0, value.Weight)
            Next

            If totalWeight <= TinyEpsilon Then
                Dim midpoint = (sorted.Count - 1) / 2.0
                Dim lowerIndex = CInt(Math.Floor(midpoint))
                Dim upperIndex = CInt(Math.Ceiling(midpoint))
                Return (sorted(lowerIndex).Value + sorted(upperIndex).Value) / 2.0
            End If

            Dim halfWeight = totalWeight / 2.0
            Dim cumulativeWeight = 0.0
            For Each value In sorted
                cumulativeWeight += Math.Max(0.0, value.Weight)
                If cumulativeWeight >= halfWeight Then
                    Return value.Value
                End If
            Next

            Return sorted(sorted.Count - 1).Value
        End Function

        Private Shared Function ComputeWeightedAngleAverage(center As Double, samples As List(Of StrokeFitSample)) As Nullable(Of Double)
            If samples Is Nothing OrElse samples.Count = 0 Then
                Return Nothing
            End If

            Dim totalWeight = 0.0
            Dim weightedDifference = 0.0
            For Each sample In samples
                Dim weight = Math.Max(0.0, sample.Weight)
                totalWeight += weight
                weightedDifference += AngleDifference(sample.AngleDegrees, center) * weight
            Next

            If totalWeight <= TinyEpsilon Then
                Return center
            End If

            Return Clamp(Wrap180(center + (weightedDifference / totalWeight)), -90.0, 90.0)
        End Function

        Private Function ComputeStability(nowMs As Double) As Nullable(Of Double)
            PruneCandidateAngles(nowMs)
            If _candidateAngles.Count < 2 Then
                Return Nothing
            End If

            Dim values(_candidateAngles.Count - 1) As Double
            For index = 0 To _candidateAngles.Count - 1
                values(index) = _candidateAngles(index).Value
            Next

            Return ComputeStandardDeviation(values)
        End Function

        Private Function ComputeQuality(hasData As Boolean, fit As FitComputation) As QualityComputation
            If Not hasData OrElse fit Is Nothing Then
                Return New QualityComputation(AngleCalibrationQualityLevel.None, AngleCalibrationQualityReason.None, 0)
            End If

            Dim leftAccepted = fit.Left.AcceptedCount
            Dim rightAccepted = fit.Right.AcceptedCount
            Dim leftInliers = fit.Left.InlierCount
            Dim rightInliers = fit.Right.InlierCount

            Dim progress = Clamp01(_swipeCount / CDbl(ResultSwipeThreshold))
            Dim acceptedTotal = Math.Max(1.0, leftAccepted + rightAccepted)
            Dim balance = 1.0 - (Math.Abs(leftAccepted - rightAccepted) / acceptedTotal)
            Dim inlierRatio = (leftInliers + rightInliers) / acceptedTotal

            Dim leftMad = If(fit.Left.MadDegrees.HasValue, fit.Left.MadDegrees.Value, 3.0)
            Dim rightMad = If(fit.Right.MadDegrees.HasValue, fit.Right.MadDegrees.Value, 3.0)
            Dim dispersion = 1.0 - Clamp01(Math.Max(leftMad, rightMad) / 3.0)

            Dim score = CInt(Math.Round((35.0 * progress) +
                                        (20.0 * balance) +
                                        (25.0 * inlierRatio) +
                                        (20.0 * dispersion)))
            score = CInt(Clamp(score, 0.0, 100.0))

            Dim reason = AngleCalibrationQualityReason.Good
            If progress < 0.3 Then
                reason = AngleCalibrationQualityReason.InsufficientProgress
            ElseIf balance < 0.6 Then
                reason = AngleCalibrationQualityReason.Imbalance
            ElseIf dispersion < 0.5 Then
                reason = AngleCalibrationQualityReason.HighDispersion
            ElseIf inlierRatio < 0.6 Then
                reason = AngleCalibrationQualityReason.TooManyOutliers
            End If

            Dim level = AngleCalibrationQualityLevel.Poor
            If score >= 85 Then
                level = AngleCalibrationQualityLevel.Excellent
            ElseIf score >= 70 Then
                level = AngleCalibrationQualityLevel.Good
            ElseIf score >= 40 Then
                level = AngleCalibrationQualityLevel.Fair
            End If

            Return New QualityComputation(level, reason, score)
        End Function

        Private Sub PruneCandidateAngles(nowMs As Double)
            Dim shouldPruneByTime = Not Double.IsNaN(nowMs) AndAlso Not Double.IsInfinity(nowMs)
            Dim cutoff = nowMs - StabilityWindowMilliseconds

            While _candidateAngles.Count > 0 AndAlso
                  ((shouldPruneByTime AndAlso _candidateAngles(0).TimestampMs < cutoff) OrElse
                   _candidateAngles.Count > StabilityMaximumSamples)
                _candidateAngles.RemoveAt(0)
            End While
        End Sub

        Private Function CreateTraceSnapshots() As IReadOnlyList(Of AngleCalibrationTraceStroke)
            Dim snapshots As New List(Of AngleCalibrationTraceStroke)()
            Dim historyStartIndex = Math.Max(0, _historicalStrokes.Count - MaximumHistoricalStrokes)

            For strokeIndex = historyStartIndex To _historicalStrokes.Count - 1
                Dim snapshot = CreateStrokeSnapshot(_historicalStrokes(strokeIndex), False)
                If snapshot IsNot Nothing Then
                    snapshots.Add(snapshot)
                End If
            Next

            Dim currentSnapshot = CreateStrokeSnapshot(CreateCurrentStrokePoints(), True)
            If currentSnapshot IsNot Nothing Then
                snapshots.Add(currentSnapshot)
            End If

            Return snapshots
        End Function

        Private Function CreateCurrentStrokePoints() As List(Of MutableTracePoint)
            If _activeStroke Is Nothing OrElse _activeStroke.Points Is Nothing OrElse _activeStroke.Points.Count = 0 Then
                Return Nothing
            End If

            If _pendingOppositeSegments.Count = 0 Then
                Return _activeStroke.Points
            End If

            Dim combined As New List(Of MutableTracePoint)(_activeStroke.Points.Count + _pendingOppositeSegments.Count)
            For Each point In _activeStroke.Points
                combined.Add(New MutableTracePoint(point.X, point.Y))
            Next

            For Each buffered In _pendingOppositeSegments
                Dim lastPoint = combined(combined.Count - 1)
                If lastPoint.X <> buffered.EndX OrElse lastPoint.Y <> buffered.EndY Then
                    combined.Add(New MutableTracePoint(buffered.EndX, buffered.EndY))
                End If
            Next

            Return combined
        End Function

        Private Sub ArchiveHistoricalStroke(points As List(Of MutableTracePoint))
            If points Is Nothing OrElse points.Count < 2 Then
                Return
            End If

            Dim snapshot As New List(Of MutableTracePoint)(points.Count)
            For Each point In points
                snapshot.Add(New MutableTracePoint(point.X, point.Y))
            Next

            _historicalStrokes.Add(snapshot)
            While _historicalStrokes.Count > MaximumHistoricalStrokes
                _historicalStrokes.RemoveAt(0)
            End While
        End Sub

        Private Sub DiscardInProgressStroke()
            _activeStroke = Nothing
            _pendingOppositeSegments.Clear()
            _oppositeDxAccum = 0.0
        End Sub

        Private Shared Function CreateStrokeSnapshot(points As List(Of MutableTracePoint), isCurrent As Boolean) As AngleCalibrationTraceStroke
            If points Is Nothing OrElse points.Count = 0 Then
                Return Nothing
            End If

            Dim result As New List(Of AngleCalibrationTracePoint)()
            If points.Count <= MaximumSnapshotPointsPerStroke Then
                For Each point In points
                    result.Add(New AngleCalibrationTracePoint(point.X, point.Y))
                Next
            Else
                Dim stride = (points.Count - 1) / CDbl(MaximumSnapshotPointsPerStroke - 1)
                For snapshotIndex = 0 To MaximumSnapshotPointsPerStroke - 1
                    Dim sourceIndex = CInt(Math.Round(snapshotIndex * stride))
                    sourceIndex = Math.Max(0, Math.Min(points.Count - 1, sourceIndex))
                    Dim point = points(sourceIndex)
                    result.Add(New AngleCalibrationTracePoint(point.X, point.Y))
                Next
            End If

            Return New AngleCalibrationTraceStroke(result, isCurrent)
        End Function

        Private Function HasData() As Boolean
            Return _historicalStrokes.Count > 0 OrElse
                   (_activeStroke IsNot Nothing AndAlso _activeStroke.Points IsNot Nothing AndAlso _activeStroke.Points.Count > 1) OrElse
                   _pendingOppositeSegments.Count > 0 OrElse
                   _swipeCount > 0 OrElse
                   _sampleCount > 0
        End Function

        Private Function ResolveStatus(hasData As Boolean, hasRecommendedAngle As Boolean) As AngleCalibrationStatus
            If Not hasData Then
                Return AngleCalibrationStatus.Empty
            End If

            If hasRecommendedAngle Then
                Return AngleCalibrationStatus.ResultReady
            End If

            If _isLocked Then
                Return AngleCalibrationStatus.Collecting
            End If

            Return AngleCalibrationStatus.Paused
        End Function

        Private Shared Function ComputeCorrectionSign(netDx As Double, netDy As Double) As Integer
            If Math.Abs(netDx) < 0.000001 Then
                Return If(netDy >= 0.0, -1, 1)
            End If

            Return If((netDx * netDy) < 0.0, 1, -1)
        End Function

        Private Shared Function ApplyDisplayAngle(value As Double) As Double
            Return Clamp(Wrap180(value), -90.0, 90.0)
        End Function

        Private Shared Function Clamp(value As Double, minimum As Double, maximum As Double) As Double
            Return Math.Max(minimum, Math.Min(maximum, value))
        End Function

        Private Shared Function Clamp01(value As Double) As Double
            Return Clamp(value, 0.0, 1.0)
        End Function

        Private Shared Function Wrap180(value As Double) As Double
            Dim wrappedValue = value

            While wrappedValue > 180.0
                wrappedValue -= 360.0
            End While

            While wrappedValue < -180.0
                wrappedValue += 360.0
            End While

            Return wrappedValue
        End Function

        Private Shared Function AngleDifference(a As Double, b As Double) As Double
            Return Wrap180(a - b)
        End Function

        Private Shared Function ComputeStandardDeviation(values As Double()) As Nullable(Of Double)
            If values Is Nothing OrElse values.Length < 2 Then
                Return Nothing
            End If

            Dim mean = 0.0
            For Each value In values
                mean += value
            Next
            mean /= values.Length

            Dim sum = 0.0
            For Each value In values
                Dim difference = value - mean
                sum += difference * difference
            Next

            Return Math.Sqrt(sum / (values.Length - 1))
        End Function
    End Class
End Namespace
