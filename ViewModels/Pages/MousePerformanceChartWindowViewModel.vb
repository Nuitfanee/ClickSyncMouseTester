Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Runtime.Versioning
Imports WpfApp1.Infrastructure
Imports WpfApp1.Models
Imports WpfApp1.Services

Namespace ViewModels.Pages
    <SupportedOSPlatform("windows")>
    Public Class MousePerformanceChartWindowViewModel
        Inherits BindableBase
        Implements IDisposable

        Public NotInheritable Class MousePerformancePlotTypeOption
            Public Sub New(plotType As MousePerformancePlotType, displayName As String)
                Me.PlotType = plotType
                Me.DisplayName = If(displayName, String.Empty)
            End Sub

            Public ReadOnly Property PlotType As MousePerformancePlotType
            Public Property DisplayName As String
        End Class

        Private ReadOnly _localization As LocalizationManager
        Private ReadOnly _preferencesStore As IMousePerformancePreferencesStore
        Private ReadOnly _plotTypeOptions As ObservableCollection(Of MousePerformancePlotTypeOption)
        Private ReadOnly _analysisOptions As MousePerformanceAnalysisOptions
        Private _snapshot As MousePerformanceSnapshot
        Private _selectedPlotType As MousePerformancePlotType
        Private _rangeStartText As String
        Private _rangeEndText As String
        Private _maximumIndex As Integer
        Private _showStem As Boolean
        Private _showLines As Boolean
        Private _renderFrame As MousePerformanceChartRenderFrame
        Private _toolbarSupplementText As String
        Private _windowTitleText As String
        Private _disposed As Boolean

        Public Sub New(Optional preferencesStore As IMousePerformancePreferencesStore = Nothing)
            _localization = LocalizationManager.Instance
            _preferencesStore = If(preferencesStore, MousePerformancePreferencesStore.Instance)
            _analysisOptions = MousePerformanceAnalysisOptions.Default
            _localization.Initialize()
            _plotTypeOptions = New ObservableCollection(Of MousePerformancePlotTypeOption)()
            _rangeStartText = "0"
            _rangeEndText = "0"
            Dim preferences = _preferencesStore.LoadPreferences()
            _selectedPlotType = preferences.ChartPlotType
            _showStem = preferences.ChartShowStem
            _showLines = preferences.ChartShowLines
            _toolbarSupplementText = String.Empty
            _renderFrame = MousePerformanceEngine.CreateChartRenderFrame(Nothing,
                                                                         _selectedPlotType,
                                                                         0,
                                                                         0,
                                                                         _showStem,
                                                                         _showLines)

            AddHandler _localization.LanguageChanged, AddressOf OnLanguageChanged

            RefreshWindowText()
            RefreshPlotOptions()
            RebuildRenderFrame()
        End Sub

        Friend ReadOnly Property PreferencesStore As IMousePerformancePreferencesStore
            Get
                Return _preferencesStore
            End Get
        End Property

        Public ReadOnly Property PlotTypeOptions As ObservableCollection(Of MousePerformancePlotTypeOption)
            Get
                Return _plotTypeOptions
            End Get
        End Property

        Public Property SelectedPlotType As MousePerformancePlotType
            Get
                Return _selectedPlotType
            End Get
            Set(value As MousePerformancePlotType)
                If SetProperty(_selectedPlotType, value) Then
                    PersistChartOptions()
                    RebuildRenderFrame()
                End If
            End Set
        End Property

        Public Property RangeStartText As String
            Get
                Return _rangeStartText
            End Get
            Set(value As String)
                SetProperty(_rangeStartText, NormalizeIndexText(value), NameOf(RangeStartText))
            End Set
        End Property

        Public Property RangeEndText As String
            Get
                Return _rangeEndText
            End Get
            Set(value As String)
                SetProperty(_rangeEndText, NormalizeIndexText(value), NameOf(RangeEndText))
            End Set
        End Property

        Public Property MaximumIndex As Integer
            Get
                Return _maximumIndex
            End Get
            Private Set(value As Integer)
                If SetProperty(_maximumIndex, Math.Max(0, value)) Then
                    RaisePropertyChanged(NameOf(MaximumIndexText))
                End If
            End Set
        End Property

        Public ReadOnly Property MaximumIndexText As String
            Get
                Return MaximumIndex.ToString(CultureInfo.InvariantCulture)
            End Get
        End Property

        Public Property ShowStem As Boolean
            Get
                Return _showStem
            End Get
            Set(value As Boolean)
                If SetProperty(_showStem, value) Then
                    PersistChartOptions()
                    RebuildRenderFrame()
                End If
            End Set
        End Property

        Public Property ShowLines As Boolean
            Get
                Return _showLines
            End Get
            Set(value As Boolean)
                If SetProperty(_showLines, value) Then
                    PersistChartOptions()
                    RebuildRenderFrame()
                End If
            End Set
        End Property

        Public Property RenderFrame As MousePerformanceChartRenderFrame
            Get
                Return _renderFrame
            End Get
            Private Set(value As MousePerformanceChartRenderFrame)
                SetProperty(_renderFrame, value)
            End Set
        End Property

        Public Property ToolbarSupplementText As String
            Get
                Return _toolbarSupplementText
            End Get
            Private Set(value As String)
                SetProperty(_toolbarSupplementText, If(value, String.Empty))
            End Set
        End Property

        Public Property WindowTitleText As String
            Get
                Return _windowTitleText
            End Get
            Private Set(value As String)
                SetProperty(_windowTitleText, value)
            End Set
        End Property

        Public Sub UpdateSnapshot(snapshot As MousePerformanceSnapshot)
            Dim previousMaximumIndex = _maximumIndex
            Dim previousStartIndex = ParseIndex(_rangeStartText, 0)
            Dim previousEndIndex = ParseIndex(_rangeEndText, previousMaximumIndex)
            Dim shouldFollowLatest = previousEndIndex >= previousMaximumIndex

            _snapshot = snapshot

            Dim nextMaximumIndex = 0
            If snapshot IsNot Nothing AndAlso snapshot.EventCount > 0 Then
                nextMaximumIndex = snapshot.EventCount - 1
            End If

            MaximumIndex = nextMaximumIndex

            Dim startIndex = Math.Max(0, Math.Min(previousStartIndex, MaximumIndex))
            Dim endIndex = Math.Max(startIndex, Math.Min(previousEndIndex, MaximumIndex))

            If shouldFollowLatest Then
                endIndex = MaximumIndex
            End If

            SetProperty(_rangeStartText, startIndex.ToString(CultureInfo.InvariantCulture), NameOf(RangeStartText))
            SetProperty(_rangeEndText, endIndex.ToString(CultureInfo.InvariantCulture), NameOf(RangeEndText))
            RebuildRenderFrame()
        End Sub

        Public Sub CommitRangeInputs()
            Dim startIndex = ParseIndex(_rangeStartText, 0)
            Dim endIndex = ParseIndex(_rangeEndText, MaximumIndex)

            startIndex = Math.Max(0, Math.Min(startIndex, MaximumIndex))
            endIndex = Math.Max(startIndex, Math.Min(endIndex, MaximumIndex))

            SetProperty(_rangeStartText, startIndex.ToString(CultureInfo.InvariantCulture), NameOf(RangeStartText))
            SetProperty(_rangeEndText, endIndex.ToString(CultureInfo.InvariantCulture), NameOf(RangeEndText))
            RebuildRenderFrame()
        End Sub

        Private Sub RebuildRenderFrame()
            Dim startIndex = ParseIndex(_rangeStartText, 0)
            Dim endIndex = ParseIndex(_rangeEndText, MaximumIndex)
            startIndex = Math.Max(0, Math.Min(startIndex, MaximumIndex))
            endIndex = Math.Max(startIndex, Math.Min(endIndex, MaximumIndex))
            ToolbarSupplementText = ResolveToolbarSupplementDescription(_selectedPlotType, _showLines)

            Dim rawFrame = MousePerformanceEngine.CreateChartRenderFrame(_snapshot,
                                                                         _selectedPlotType,
                                                                         startIndex,
                                                                         endIndex,
                                                                         _showStem,
                                                                         _showLines)
            RenderFrame = LocalizeFrame(rawFrame)
        End Sub

        Private Function LocalizeFrame(frame As MousePerformanceChartRenderFrame) As MousePerformanceChartRenderFrame
            If frame Is Nothing Then
                Return Nothing
            End If

            Dim title = ResolvePlotDisplayName(frame.PlotType)
            Dim subtitle = ResolveSubtitle(_snapshot)
            Dim description = ResolvePlotDescription(frame.PlotType, frame.ShowLines, _snapshot)
            Dim xAxisTitle = ResolveXAxisTitle(frame.PlotType)
            Dim yAxisTitle = ResolveYAxisTitle(frame.PlotType)
            Dim message = ResolveUnavailableMessage(frame.PlotType)

            If frame.IsAvailable Then
                message = String.Empty
            End If

            Return New MousePerformanceChartRenderFrame(frame.PlotType,
                                                        title,
                                                        subtitle,
                                                        description,
                                                        xAxisTitle,
                                                        yAxisTitle,
                                                        frame.IsAvailable,
                                                        message,
                                                        frame.StartIndex,
                                                        frame.EndIndex,
                                                        frame.ShowStem,
                                                        frame.ShowLines,
                                                        frame.XMinimum,
                                                        frame.XMaximum,
                                                        frame.YMinimum,
                                                        frame.YMaximum,
                                                        frame.Series)
        End Function

        Private Sub RefreshPlotOptions()
            Dim existing = _plotTypeOptions.ToDictionary(Function(item) item.PlotType)
            _plotTypeOptions.Clear()

            For Each plotType In [Enum].GetValues(GetType(MousePerformancePlotType))
                Dim value = CType(plotType, MousePerformancePlotType)
                Dim optionValue As MousePerformancePlotTypeOption = Nothing
                If existing.ContainsKey(value) Then
                    optionValue = existing(value)
                    optionValue.DisplayName = ResolvePlotDisplayName(value)
                Else
                    optionValue = New MousePerformancePlotTypeOption(value, ResolvePlotDisplayName(value))
                End If

                _plotTypeOptions.Add(optionValue)
            Next
        End Sub

        Private Sub RefreshWindowText()
            WindowTitleText = L("MousePerformance.Chart.WindowTitle")
        End Sub

        Private Function ResolvePlotDisplayName(plotType As MousePerformancePlotType) As String
            Select Case plotType
                Case MousePerformancePlotType.XCountVsTime
                    Return L("MousePerformance.Chart.Plot.XCountVsTime")
                Case MousePerformancePlotType.YCountVsTime
                    Return L("MousePerformance.Chart.Plot.YCountVsTime")
                Case MousePerformancePlotType.XYCountVsTime
                    Return L("MousePerformance.Chart.Plot.XYCountVsTime")
                Case MousePerformancePlotType.IntervalVsTime
                    Return L("MousePerformance.Chart.Plot.IntervalVsTime")
                Case MousePerformancePlotType.XVelocityVsTime
                    Return L("MousePerformance.Chart.Plot.XVelocityVsTime")
                Case MousePerformancePlotType.YVelocityVsTime
                    Return L("MousePerformance.Chart.Plot.YVelocityVsTime")
                Case MousePerformancePlotType.XYVelocityVsTime
                    Return L("MousePerformance.Chart.Plot.XYVelocityVsTime")
                Case MousePerformancePlotType.FrequencyVsTime
                    Return L("MousePerformance.Chart.Plot.FrequencyVsTime")
                Case MousePerformancePlotType.XSumVsTime
                    Return L("MousePerformance.Chart.Plot.XSumVsTime")
                Case MousePerformancePlotType.YSumVsTime
                    Return L("MousePerformance.Chart.Plot.YSumVsTime")
                Case MousePerformancePlotType.XYSumVsTime
                    Return L("MousePerformance.Chart.Plot.XYSumVsTime")
                Case Else
                    Return L("MousePerformance.Chart.Plot.XVsY")
            End Select
        End Function

        Private Function ResolveXAxisTitle(plotType As MousePerformancePlotType) As String
            If plotType = MousePerformancePlotType.XVsY Then
                Return L("MousePerformance.Chart.Axis.XCounts")
            End If

            Return L("MousePerformance.Chart.Axis.Time")
        End Function

        Private Function ResolveYAxisTitle(plotType As MousePerformancePlotType) As String
            Select Case plotType
                Case MousePerformancePlotType.XCountVsTime
                    Return L("MousePerformance.Chart.Axis.XCounts")
                Case MousePerformancePlotType.YCountVsTime
                    Return L("MousePerformance.Chart.Axis.YCounts")
                Case MousePerformancePlotType.XYCountVsTime
                    Return L("MousePerformance.Chart.Axis.XYCounts")
                Case MousePerformancePlotType.IntervalVsTime
                    Return L("MousePerformance.Chart.Axis.Interval")
                Case MousePerformancePlotType.FrequencyVsTime
                    Return L("MousePerformance.Chart.Axis.Frequency")
                Case MousePerformancePlotType.XVelocityVsTime
                    Return L("MousePerformance.Chart.Axis.XVelocity")
                Case MousePerformancePlotType.YVelocityVsTime
                    Return L("MousePerformance.Chart.Axis.YVelocity")
                Case MousePerformancePlotType.XYVelocityVsTime
                    Return L("MousePerformance.Chart.Axis.XYVelocity")
                Case MousePerformancePlotType.XSumVsTime
                    Return L("MousePerformance.Chart.Axis.XSum")
                Case MousePerformancePlotType.YSumVsTime
                    Return L("MousePerformance.Chart.Axis.YSum")
                Case MousePerformancePlotType.XYSumVsTime
                    Return L("MousePerformance.Chart.Axis.XYSum")
                Case Else
                    Return L("MousePerformance.Chart.Axis.YCounts")
            End Select
        End Function

        Private Function ResolvePlotDescription(plotType As MousePerformancePlotType,
                                               showLines As Boolean,
                                               snapshot As MousePerformanceSnapshot) As String
            Dim segments As New List(Of String)()

            Select Case plotType
                Case MousePerformancePlotType.XCountVsTime
                    segments.Add(L("MousePerformance.Chart.Description.XCountVsTime"))
                Case MousePerformancePlotType.YCountVsTime
                    segments.Add(L("MousePerformance.Chart.Description.YCountVsTime"))
                Case MousePerformancePlotType.XYCountVsTime
                    segments.Add(L("MousePerformance.Chart.Description.XYCountVsTime"))
                Case MousePerformancePlotType.IntervalVsTime
                    segments.Add(L("MousePerformance.Chart.Description.IntervalVsTime"))
                Case MousePerformancePlotType.FrequencyVsTime
                    segments.Add(L("MousePerformance.Chart.Description.FrequencyVsTime"))
                Case MousePerformancePlotType.XVelocityVsTime
                    segments.Add(L("MousePerformance.Chart.Description.XVelocityVsTime"))
                Case MousePerformancePlotType.YVelocityVsTime
                    segments.Add(L("MousePerformance.Chart.Description.YVelocityVsTime"))
                Case MousePerformancePlotType.XYVelocityVsTime
                    segments.Add(L("MousePerformance.Chart.Description.XYVelocityVsTime"))
                Case MousePerformancePlotType.XSumVsTime
                    segments.Add(L("MousePerformance.Chart.Description.XSumVsTime"))
                Case MousePerformancePlotType.YSumVsTime
                    segments.Add(L("MousePerformance.Chart.Description.YSumVsTime"))
                Case MousePerformancePlotType.XYSumVsTime
                    segments.Add(L("MousePerformance.Chart.Description.XYSumVsTime"))
                Case Else
                    segments.Add(L("MousePerformance.Chart.Description.XVsY"))
            End Select

            Dim qualityWarning = ResolveQualityWarning(snapshot)
            If Not String.IsNullOrWhiteSpace(qualityWarning) Then
                segments.Add(qualityWarning)
            End If

            Return String.Join(" | ", segments.Where(Function(segment) Not String.IsNullOrWhiteSpace(segment)))
        End Function

        Private Function ResolveToolbarSupplementDescription(plotType As MousePerformancePlotType,
                                                             showLines As Boolean) As String
            Dim segments As New List(Of String)()

            If plotType = MousePerformancePlotType.XVsY Then
                segments.Add(L("MousePerformance.Chart.Description.Semantics.Trajectory"))
            ElseIf showLines Then
                segments.Add(L("MousePerformance.Chart.Description.Semantics.RawLine"))
            Else
                segments.Add(L("MousePerformance.Chart.Description.Semantics.TrendLine",
                               _analysisOptions.TrendWindowMs.ToString("0.#", CultureInfo.InvariantCulture)))
            End If

            Return String.Join(" | ", segments.Where(Function(segment) Not String.IsNullOrWhiteSpace(segment)))
        End Function

        Private Function ResolveSubtitle(snapshot As MousePerformanceSnapshot) As String
            If snapshot Is Nothing OrElse Not snapshot.EffectiveCpi.HasValue Then
                Return L("MousePerformance.Chart.Subtitle.NoCpi")
            End If

            Return L("MousePerformance.Chart.Subtitle.WithCpi",
                     snapshot.EffectiveCpi.Value.ToString("0.##", CultureInfo.InvariantCulture))
        End Function

        Private Function ResolveUnavailableMessage(plotType As MousePerformancePlotType) As String
            If _snapshot Is Nothing OrElse _snapshot.Events Is Nothing OrElse _snapshot.Events.Count = 0 Then
                Return L("MousePerformance.Chart.Unavailable.NoData")
            End If

            If plotType <> MousePerformancePlotType.XCountVsTime AndAlso
               plotType <> MousePerformancePlotType.YCountVsTime AndAlso
               plotType <> MousePerformancePlotType.XYCountVsTime AndAlso
               plotType <> MousePerformancePlotType.XSumVsTime AndAlso
               plotType <> MousePerformancePlotType.YSumVsTime AndAlso
               plotType <> MousePerformancePlotType.XYSumVsTime AndAlso
               plotType <> MousePerformancePlotType.XVsY AndAlso
               _snapshot.Events.Count < 2 Then
                Return L("MousePerformance.Chart.Unavailable.InsufficientMotion")
            End If

            If (plotType = MousePerformancePlotType.XVelocityVsTime OrElse
                plotType = MousePerformancePlotType.YVelocityVsTime OrElse
                plotType = MousePerformancePlotType.XYVelocityVsTime) AndAlso
               (_snapshot Is Nothing OrElse Not _snapshot.CanComputeVelocity) Then
                Return L("MousePerformance.Chart.Unavailable.InvalidCpi")
            End If

            Return L("MousePerformance.Chart.Unavailable.Generic")
        End Function

        Private Function ResolveQualityWarning(snapshot As MousePerformanceSnapshot) As String
            If snapshot Is Nothing OrElse snapshot.DataQuality Is Nothing Then
                Return String.Empty
            End If

            Dim quality = snapshot.DataQuality
            If quality.QualityLevel <> MousePerformanceDataQualityLevel.Degraded Then
                Return String.Empty
            End If

            Return L("MousePerformance.Chart.Description.Semantics.Degraded",
                     quality.DroppedPacketCount.ToString(CultureInfo.InvariantCulture),
                     quality.OutOfOrderTimestampCount.ToString(CultureInfo.InvariantCulture),
                     quality.ZeroIntervalCount.ToString(CultureInfo.InvariantCulture))
        End Function

        Private Sub PersistChartOptions()
            _preferencesStore.SaveChartOptions(_selectedPlotType, _showStem, _showLines)
        End Sub

        Private Sub OnLanguageChanged(sender As Object, e As EventArgs)
            RefreshWindowText()
            RefreshPlotOptions()
            RebuildRenderFrame()
        End Sub

        Private Function L(key As String, ParamArray args() As Object) As String
            Return _localization.GetString(key, args)
        End Function

        Private Shared Function NormalizeIndexText(text As String) As String
            If String.IsNullOrEmpty(text) Then
                Return String.Empty
            End If

            Dim builder As New System.Text.StringBuilder(text.Length)
            For Each ch In text
                If Char.IsDigit(ch) Then
                    builder.Append(ch)
                End If
            Next

            Return builder.ToString()
        End Function

        Private Shared Function ParseIndex(text As String, fallback As Integer) As Integer
            Dim value = 0
            If String.IsNullOrWhiteSpace(text) OrElse
               Not Integer.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, value) Then
                Return fallback
            End If

            Return value
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then
                Return
            End If

            _disposed = True
            RemoveHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
        End Sub
    End Class
End Namespace
