Imports WpfApp1.Models

Namespace Services
    Public Interface IMousePerformanceSettingsAccessor
        Property MousePerformanceLastCpi As Double
        Property MousePerformanceChartPlotType As Integer
        Property MousePerformanceChartShowStem As Boolean
        Property MousePerformanceChartShowLines As Boolean
        Property MousePerformanceChartHasSavedBounds As Boolean
        Property MousePerformanceChartLeft As Double
        Property MousePerformanceChartTop As Double
        Property MousePerformanceChartWidth As Double
        Property MousePerformanceChartHeight As Double
        Property MousePerformanceChartIsMaximized As Boolean
        Sub Save()
    End Interface

    Public Interface IMousePerformancePreferencesStore
        Function LoadPreferences() As MousePerformancePreferences
        Sub SaveLastCpi(value As Double)
        Sub SaveChartOptions(plotType As MousePerformancePlotType, showStem As Boolean, showLines As Boolean)
        Sub SaveChartWindowPlacement(placement As MousePerformanceChartWindowPlacement)
    End Interface

    Public NotInheritable Class MousePerformancePreferencesStore
        Implements IMousePerformancePreferencesStore

        Private Const DefaultLastCpi As Double = 800.0

        Private Shared ReadOnly _instance As IMousePerformancePreferencesStore = New MousePerformancePreferencesStore()
        Private ReadOnly _settings As IMousePerformanceSettingsAccessor

        Private Sub New()
            Me.New(New MySettingsMousePerformanceSettingsAccessor())
        End Sub

        Public Sub New(settingsAccessor As IMousePerformanceSettingsAccessor)
            _settings = If(settingsAccessor, New MySettingsMousePerformanceSettingsAccessor())
        End Sub

        Public Shared ReadOnly Property Instance As IMousePerformancePreferencesStore
            Get
                Return _instance
            End Get
        End Property

        Public Function LoadPreferences() As MousePerformancePreferences Implements IMousePerformancePreferencesStore.LoadPreferences
            Try
                Dim lastCpi As Nullable(Of Double) = Nothing
                If IsFinitePositive(_settings.MousePerformanceLastCpi) Then
                    lastCpi = _settings.MousePerformanceLastCpi
                End If

                Dim plotTypeValue = MousePerformancePlotType.XCountVsTime
                Dim rawPlotType = _settings.MousePerformanceChartPlotType
                If [Enum].IsDefined(GetType(MousePerformancePlotType), rawPlotType) Then
                    plotTypeValue = CType(rawPlotType, MousePerformancePlotType)
                End If

                Dim placement = New MousePerformanceChartWindowPlacement(_settings.MousePerformanceChartHasSavedBounds,
                                                                         _settings.MousePerformanceChartLeft,
                                                                         _settings.MousePerformanceChartTop,
                                                                         _settings.MousePerformanceChartWidth,
                                                                         _settings.MousePerformanceChartHeight,
                                                                         _settings.MousePerformanceChartIsMaximized)

                Return New MousePerformancePreferences(lastCpi,
                                                       plotTypeValue,
                                                       _settings.MousePerformanceChartShowStem,
                                                       _settings.MousePerformanceChartShowLines,
                                                       placement)
            Catch
                Return New MousePerformancePreferences(DefaultLastCpi,
                                                       MousePerformancePlotType.XCountVsTime,
                                                       False,
                                                       True,
                                                       New MousePerformanceChartWindowPlacement(False, 0.0, 0.0, 0.0, 0.0, False))
            End Try
        End Function

        Public Sub SaveLastCpi(value As Double) Implements IMousePerformancePreferencesStore.SaveLastCpi
            If Not IsFinitePositive(value) Then
                Return
            End If

            Try
                _settings.MousePerformanceLastCpi = value
                _settings.Save()
            Catch
            End Try
        End Sub

        Public Sub SaveChartOptions(plotType As MousePerformancePlotType,
                                    showStem As Boolean,
                                    showLines As Boolean) Implements IMousePerformancePreferencesStore.SaveChartOptions
            Try
                _settings.MousePerformanceChartPlotType = CInt(plotType)
                _settings.MousePerformanceChartShowStem = showStem
                _settings.MousePerformanceChartShowLines = showLines
                _settings.Save()
            Catch
            End Try
        End Sub

        Public Sub SaveChartWindowPlacement(placement As MousePerformanceChartWindowPlacement) Implements IMousePerformancePreferencesStore.SaveChartWindowPlacement
            Try
                If placement Is Nothing OrElse Not placement.HasSavedBounds Then
                    _settings.MousePerformanceChartHasSavedBounds = False
                    _settings.MousePerformanceChartIsMaximized = False
                    _settings.Save()
                    Return
                End If

                _settings.MousePerformanceChartHasSavedBounds = True
                _settings.MousePerformanceChartLeft = placement.Left
                _settings.MousePerformanceChartTop = placement.Top
                _settings.MousePerformanceChartWidth = placement.Width
                _settings.MousePerformanceChartHeight = placement.Height
                _settings.MousePerformanceChartIsMaximized = placement.IsMaximized
                _settings.Save()
            Catch
            End Try
        End Sub

        Private Shared Function IsFinitePositive(value As Double) As Boolean
            Return value > 0.0 AndAlso Not Double.IsNaN(value) AndAlso Not Double.IsInfinity(value)
        End Function

        Private NotInheritable Class MySettingsMousePerformanceSettingsAccessor
            Implements IMousePerformanceSettingsAccessor

            Public Property MousePerformanceLastCpi As Double Implements IMousePerformanceSettingsAccessor.MousePerformanceLastCpi
                Get
                    Return My.Settings.MousePerformanceLastCpi
                End Get
                Set(value As Double)
                    My.Settings.MousePerformanceLastCpi = value
                End Set
            End Property

            Public Property MousePerformanceChartPlotType As Integer Implements IMousePerformanceSettingsAccessor.MousePerformanceChartPlotType
                Get
                    Return My.Settings.MousePerformanceChartPlotType
                End Get
                Set(value As Integer)
                    My.Settings.MousePerformanceChartPlotType = value
                End Set
            End Property

            Public Property MousePerformanceChartShowStem As Boolean Implements IMousePerformanceSettingsAccessor.MousePerformanceChartShowStem
                Get
                    Return My.Settings.MousePerformanceChartShowStem
                End Get
                Set(value As Boolean)
                    My.Settings.MousePerformanceChartShowStem = value
                End Set
            End Property

            Public Property MousePerformanceChartShowLines As Boolean Implements IMousePerformanceSettingsAccessor.MousePerformanceChartShowLines
                Get
                    Return My.Settings.MousePerformanceChartShowLines
                End Get
                Set(value As Boolean)
                    My.Settings.MousePerformanceChartShowLines = value
                End Set
            End Property

            Public Property MousePerformanceChartHasSavedBounds As Boolean Implements IMousePerformanceSettingsAccessor.MousePerformanceChartHasSavedBounds
                Get
                    Return My.Settings.MousePerformanceChartHasSavedBounds
                End Get
                Set(value As Boolean)
                    My.Settings.MousePerformanceChartHasSavedBounds = value
                End Set
            End Property

            Public Property MousePerformanceChartLeft As Double Implements IMousePerformanceSettingsAccessor.MousePerformanceChartLeft
                Get
                    Return My.Settings.MousePerformanceChartLeft
                End Get
                Set(value As Double)
                    My.Settings.MousePerformanceChartLeft = value
                End Set
            End Property

            Public Property MousePerformanceChartTop As Double Implements IMousePerformanceSettingsAccessor.MousePerformanceChartTop
                Get
                    Return My.Settings.MousePerformanceChartTop
                End Get
                Set(value As Double)
                    My.Settings.MousePerformanceChartTop = value
                End Set
            End Property

            Public Property MousePerformanceChartWidth As Double Implements IMousePerformanceSettingsAccessor.MousePerformanceChartWidth
                Get
                    Return My.Settings.MousePerformanceChartWidth
                End Get
                Set(value As Double)
                    My.Settings.MousePerformanceChartWidth = value
                End Set
            End Property

            Public Property MousePerformanceChartHeight As Double Implements IMousePerformanceSettingsAccessor.MousePerformanceChartHeight
                Get
                    Return My.Settings.MousePerformanceChartHeight
                End Get
                Set(value As Double)
                    My.Settings.MousePerformanceChartHeight = value
                End Set
            End Property

            Public Property MousePerformanceChartIsMaximized As Boolean Implements IMousePerformanceSettingsAccessor.MousePerformanceChartIsMaximized
                Get
                    Return My.Settings.MousePerformanceChartIsMaximized
                End Get
                Set(value As Boolean)
                    My.Settings.MousePerformanceChartIsMaximized = value
                End Set
            End Property

            Public Sub Save() Implements IMousePerformanceSettingsAccessor.Save
                My.Settings.Save()
            End Sub
        End Class
    End Class
End Namespace
