Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Runtime.Versioning
Imports WpfApp1.Infrastructure
Imports WpfApp1.Models
Imports WpfApp1.Navigation
Imports WpfApp1.Services
Imports WpfApp1.ViewModels.Pages

Namespace ViewModels
    <SupportedOSPlatform("windows")>
    Public Class ShellViewModel
        Inherits BindableBase
        Implements IDisposable

        Private ReadOnly _localization As LocalizationManager
        Private ReadOnly _themeManager As ThemeManager
        Private ReadOnly _pages As ObservableCollection(Of AppPageDescriptor)
        Private ReadOnly _toggleLanguageCommand As DelegateCommand
        Private ReadOnly _toggleThemeCommand As DelegateCommand
        Private ReadOnly _openNavigationMenuCommand As DelegateCommand
        Private ReadOnly _closeNavigationMenuCommand As DelegateCommand
        Private ReadOnly _navigateToPageCommand As DelegateCommand
        Private ReadOnly _rawInputBroker As IRawInputBroker
        Private ReadOnly _pagePollingDashboard As MainWindowViewModel
        Private ReadOnly _pageMousePerformance As MousePerformancePageViewModel
        Private ReadOnly _pageKeyDetection As KeyDetectionPageViewModel
        Private ReadOnly _pageSensitivityMatching As SensitivityMatchingPageViewModel
        Private ReadOnly _pageAngleCalibration As AngleCalibrationPageViewModel

        Private _languageToggleText As String
        Private _languageToggleKeyText As String
        Private _currentLanguageKeyText As String
        Private _themeToggleText As String
        Private _themeToggleKeyText As String
        Private _currentThemeKeyText As String
        Private _currentPage As Object
        Private _currentPageDescriptor As AppPageDescriptor
        Private _isNavigationMenuOpen As Boolean
        Private _disposed As Boolean

        Public Sub New()
            _localization = LocalizationManager.Instance
            _themeManager = ThemeManager.Instance
            _localization.Initialize()

            _toggleLanguageCommand = New DelegateCommand(AddressOf RequestToggleLanguage)
            _toggleThemeCommand = New DelegateCommand(AddressOf RequestToggleTheme)
            _openNavigationMenuCommand = New DelegateCommand(AddressOf RequestOpenNavigationMenu, AddressOf CanOpenNavigationMenu)
            _closeNavigationMenuCommand = New DelegateCommand(AddressOf RequestCloseNavigationMenu, AddressOf CanCloseNavigationMenu)
            _navigateToPageCommand = New DelegateCommand(AddressOf RequestNavigateToPage, AddressOf CanNavigateToPage)

            _rawInputBroker = New RawInputBroker()
            _pages = New ObservableCollection(Of AppPageDescriptor)()
            _pagePollingDashboard = New MainWindowViewModel(_rawInputBroker)
            _pageMousePerformance = New MousePerformancePageViewModel(_rawInputBroker)
            _pageKeyDetection = New KeyDetectionPageViewModel(_rawInputBroker)
            _pageSensitivityMatching = New SensitivityMatchingPageViewModel(_rawInputBroker)
            _pageAngleCalibration = New AngleCalibrationPageViewModel(_rawInputBroker)

            RegisterPage(New AppPageDescriptor(AppPageKey.PollingDashboard,
                                               "Navigation.Page.PollingDashboard",
                                               "轮询率检测",
                                               "Navigation.Page.PollingDashboard.Summary",
                                               "实时显示 Raw Input 轮询率、历史曲线和采集控制的主工作页。",
                                               "Navigation.Page.PollingDashboard.MenuMeta",
                                               "POLLING RATE TEST",
                                               _pagePollingDashboard))
            RegisterPage(New AppPageDescriptor(AppPageKey.MousePerformance,
                                               "Navigation.Page.MousePerformance",
                                               "Mouse Performance",
                                               "Navigation.Page.MousePerformance.Summary",
                                               "Collect and inspect raw mouse performance sessions with live plots.",
                                               "Navigation.Page.MousePerformance.MenuMeta",
                                               "MOUSE PERFORMANCE ANALYSIS",
                                               _pageMousePerformance))
            RegisterPage(New AppPageDescriptor(AppPageKey.KeyDetection,
                                               "Navigation.Page.KeyDetection",
                                               "按键双击检测",
                                               "Navigation.Page.KeyDetection.Summary",
                                               "实时检测鼠标按键、滚轮与自定义键位的按下时序、双击阈值和状态变化。",
                                               "Navigation.Page.KeyDetection.MenuMeta",
                                               "BUTTON / WHEEL / KEY CHECK",
                                               _pageKeyDetection))
            RegisterPage(New AppPageDescriptor(AppPageKey.SensitivityMatching,
                                               "Navigation.Page.SensitivityMatching",
                                               "鼠标灵敏度复制",
                                               "Navigation.Page.SensitivityMatching.Summary",
                                               "使用两只鼠标完成三轮 Raw Input 测量，生成推荐目标 DPI 与调整倍率。",
                                               "Navigation.Page.SensitivityMatching.MenuMeta",
                                               "SENSITIVITY MATCH TOOL",
                                               _pageSensitivityMatching))
            RegisterPage(New AppPageDescriptor(AppPageKey.AngleCalibration,
                                               "Navigation.Page.AngleCalibration",
                                               "传感器角度校准",
                                               "Navigation.Page.AngleCalibration.Summary",
                                               "测量鼠标水平滑动轨迹，生成推荐角度并观察稳定度与轨迹质量。",
                                               "Navigation.Page.AngleCalibration.MenuMeta",
                                               "ANGLE CALIBRATION",
                                               _pageAngleCalibration))

            _pages.Move(3, 1)
            _pages.Move(4, 3)

            AddHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            AddHandler _themeManager.ThemeChanged, AddressOf OnThemeChanged

            RefreshPageLocalization()
            SwitchCurrentPage(ResolvePageDescriptor(AppPageKey.PollingDashboard), raiseChanged:=False)
            UpdateLanguageToggleText()
            UpdateThemeToggleText()
        End Sub

        Public Event CurrentPageChanged As EventHandler

        Public ReadOnly Property Pages As ObservableCollection(Of AppPageDescriptor)
            Get
                Return _pages
            End Get
        End Property

        Public Property CurrentPage As Object
            Get
                Return _currentPage
            End Get
            Private Set(value As Object)
                SetProperty(_currentPage, value)
            End Set
        End Property

        Public Property CurrentPageDescriptor As AppPageDescriptor
            Get
                Return _currentPageDescriptor
            End Get
            Private Set(value As AppPageDescriptor)
                SetProperty(_currentPageDescriptor, value)
            End Set
        End Property

        Public ReadOnly Property CurrentPageKey As AppPageKey
            Get
                If _currentPageDescriptor Is Nothing Then
                    Return AppPageKey.PollingDashboard
                End If

                Return _currentPageDescriptor.PageKey
            End Get
        End Property

        Public ReadOnly Property CurrentPageTitle As String
            Get
                If _currentPageDescriptor Is Nothing Then
                    Return String.Empty
                End If

                Return _currentPageDescriptor.DisplayTitle
            End Get
        End Property

        Public ReadOnly Property CurrentPageSummary As String
            Get
                If _currentPageDescriptor Is Nothing Then
                    Return String.Empty
                End If

                Return _currentPageDescriptor.DisplaySummary
            End Get
        End Property

        Public ReadOnly Property IsChineseLanguageCurrent As Boolean
            Get
                Dim currentLanguage = _localization.CurrentLanguage
                Return currentLanguage IsNot Nothing AndAlso
                       currentLanguage.CultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsEnglishLanguageCurrent As Boolean
            Get
                Dim currentLanguage = _localization.CurrentLanguage
                Return currentLanguage IsNot Nothing AndAlso
                       currentLanguage.CultureName.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsLightThemeCurrent As Boolean
            Get
                Return _themeManager.CurrentTheme = AppTheme.Light
            End Get
        End Property

        Public ReadOnly Property IsDarkThemeCurrent As Boolean
            Get
                Return _themeManager.CurrentTheme = AppTheme.Dark
            End Get
        End Property

        Public Property IsNavigationMenuOpen As Boolean
            Get
                Return _isNavigationMenuOpen
            End Get
            Private Set(value As Boolean)
                If SetProperty(_isNavigationMenuOpen, value) Then
                    _openNavigationMenuCommand.RaiseCanExecuteChanged()
                    _closeNavigationMenuCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Public Property LanguageToggleText As String
            Get
                Return _languageToggleText
            End Get
            Private Set(value As String)
                SetProperty(_languageToggleText, value)
            End Set
        End Property

        Public Property LanguageToggleKeyText As String
            Get
                Return _languageToggleKeyText
            End Get
            Private Set(value As String)
                SetProperty(_languageToggleKeyText, value)
            End Set
        End Property

        Public Property CurrentLanguageKeyText As String
            Get
                Return _currentLanguageKeyText
            End Get
            Private Set(value As String)
                SetProperty(_currentLanguageKeyText, value)
            End Set
        End Property

        Public Property ThemeToggleText As String
            Get
                Return _themeToggleText
            End Get
            Private Set(value As String)
                SetProperty(_themeToggleText, value)
            End Set
        End Property

        Public Property ThemeToggleKeyText As String
            Get
                Return _themeToggleKeyText
            End Get
            Private Set(value As String)
                SetProperty(_themeToggleKeyText, value)
            End Set
        End Property

        Public Property CurrentThemeKeyText As String
            Get
                Return _currentThemeKeyText
            End Get
            Private Set(value As String)
                SetProperty(_currentThemeKeyText, value)
            End Set
        End Property

        Public ReadOnly Property ToggleLanguageCommand As DelegateCommand
            Get
                Return _toggleLanguageCommand
            End Get
        End Property

        Public ReadOnly Property ToggleThemeCommand As DelegateCommand
            Get
                Return _toggleThemeCommand
            End Get
        End Property

        Public ReadOnly Property OpenNavigationMenuCommand As DelegateCommand
            Get
                Return _openNavigationMenuCommand
            End Get
        End Property

        Public ReadOnly Property CloseNavigationMenuCommand As DelegateCommand
            Get
                Return _closeNavigationMenuCommand
            End Get
        End Property

        Public ReadOnly Property NavigateToPageCommand As DelegateCommand
            Get
                Return _navigateToPageCommand
            End Get
        End Property

        Public ReadOnly Property ActiveCapturePage As ICaptureSessionPageViewModel
            Get
                Return TryCast(CurrentPage, ICaptureSessionPageViewModel)
            End Get
        End Property

        Public Sub OpenNavigationMenu()
            If Not CanOpenNavigationMenu() Then
                Return
            End If

            IsNavigationMenuOpen = True
        End Sub

        Public Sub CloseNavigationMenu()
            If Not IsNavigationMenuOpen Then
                Return
            End If

            IsNavigationMenuOpen = False
        End Sub

        Private Sub OnLanguageChanged(sender As Object, e As EventArgs)
            RefreshPageLocalization()
            UpdateLanguageToggleText()
            UpdateThemeToggleText()
            RaisePropertyChanged(NameOf(IsChineseLanguageCurrent))
            RaisePropertyChanged(NameOf(IsEnglishLanguageCurrent))
            RaisePropertyChanged(NameOf(CurrentPageTitle))
            RaisePropertyChanged(NameOf(CurrentPageSummary))
        End Sub

        Private Sub OnThemeChanged(sender As Object, e As EventArgs)
            UpdateThemeToggleText()
            RaisePropertyChanged(NameOf(IsLightThemeCurrent))
            RaisePropertyChanged(NameOf(IsDarkThemeCurrent))
        End Sub

        Private Sub RequestToggleLanguage()
            Dim nextLanguage = GetNextLanguage()
            If nextLanguage Is Nothing Then
                Return
            End If

            _localization.SetLanguage(nextLanguage.CultureName)
        End Sub

        Private Sub RequestToggleTheme()
            _themeManager.ToggleTheme()
        End Sub

        Private Sub RequestOpenNavigationMenu()
            OpenNavigationMenu()
        End Sub

        Private Function CanOpenNavigationMenu() As Boolean
            Return Not IsNavigationMenuOpen AndAlso Not IsNavigationBlocked()
        End Function

        Private Sub RequestCloseNavigationMenu()
            CloseNavigationMenu()
        End Sub

        Private Function CanCloseNavigationMenu() As Boolean
            Return IsNavigationMenuOpen
        End Function

        Private Sub RequestNavigateToPage(parameter As Object)
            Dim targetPage = ResolvePageDescriptor(parameter)
            If targetPage Is Nothing Then
                Return
            End If

            If ReferenceEquals(targetPage, _currentPageDescriptor) Then
                IsNavigationMenuOpen = False
                Return
            End If

            SwitchCurrentPage(targetPage, raiseChanged:=True)
            IsNavigationMenuOpen = False
        End Sub

        Private Function CanNavigateToPage(parameter As Object) As Boolean
            Dim targetPage = ResolvePageDescriptor(parameter)
            Return targetPage IsNot Nothing AndAlso
                   Not IsNavigationBlocked()
        End Function

        Private Function IsNavigationBlocked() As Boolean
            Dim capturePage = ActiveCapturePage
            Return capturePage IsNot Nothing AndAlso capturePage.IsLocked
        End Function

        Private Function ResolvePageDescriptor(parameter As Object) As AppPageDescriptor
            If parameter Is Nothing Then
                Return Nothing
            End If

            Dim descriptor = TryCast(parameter, AppPageDescriptor)
            If descriptor IsNot Nothing Then
                Return _pages.FirstOrDefault(Function(item) item.PageKey = descriptor.PageKey)
            End If

            If TypeOf parameter Is AppPageKey Then
                Dim pageKey = CType(parameter, AppPageKey)
                Return _pages.FirstOrDefault(Function(item) item.PageKey = pageKey)
            End If

            Dim pageKeyValue As AppPageKey
            If [Enum].TryParse(parameter.ToString(), True, pageKeyValue) Then
                Return _pages.FirstOrDefault(Function(item) item.PageKey = pageKeyValue)
            End If

            Return Nothing
        End Function

        Private Sub SwitchCurrentPage(targetPage As AppPageDescriptor, raiseChanged As Boolean)
            If targetPage Is Nothing Then
                Return
            End If

            If ReferenceEquals(_currentPageDescriptor, targetPage) Then
                If raiseChanged Then
                    RaiseEvent CurrentPageChanged(Me, EventArgs.Empty)
                End If

                Return
            End If

            Dim previousPage = CurrentPage

            DetachCurrentPageNotifications()

            If _currentPageDescriptor IsNot Nothing Then
                _currentPageDescriptor.SetCurrentPageState(False)
            End If

            CurrentPageDescriptor = targetPage
            CurrentPage = targetPage.PageViewModel
            targetPage.SetCurrentPageState(True)

            ResetPageState(previousPage)
            AttachCurrentPageNotifications()

            RaisePropertyChanged(NameOf(CurrentPageKey))
            RaisePropertyChanged(NameOf(CurrentPageTitle))
            RaisePropertyChanged(NameOf(CurrentPageSummary))
            _openNavigationMenuCommand.RaiseCanExecuteChanged()
            _closeNavigationMenuCommand.RaiseCanExecuteChanged()
            _navigateToPageCommand.RaiseCanExecuteChanged()

            If raiseChanged Then
                RaiseEvent CurrentPageChanged(Me, EventArgs.Empty)
            End If
        End Sub

        Private Shared Sub ResetPageState(pageViewModel As Object)
            Dim resettablePage = TryCast(pageViewModel, INavigationResettablePageViewModel)
            If resettablePage Is Nothing Then
                Return
            End If

            resettablePage.ResetToDefaultState()
        End Sub

        Private Sub AttachCurrentPageNotifications()
            Dim notifier = TryCast(CurrentPage, INotifyPropertyChanged)
            If notifier Is Nothing Then
                Return
            End If

            AddHandler notifier.PropertyChanged, AddressOf OnCurrentPagePropertyChanged
        End Sub

        Private Sub DetachCurrentPageNotifications()
            Dim notifier = TryCast(CurrentPage, INotifyPropertyChanged)
            If notifier Is Nothing Then
                Return
            End If

            RemoveHandler notifier.PropertyChanged, AddressOf OnCurrentPagePropertyChanged
        End Sub

        Private Sub OnCurrentPagePropertyChanged(sender As Object, e As PropertyChangedEventArgs)
            If String.IsNullOrEmpty(e.PropertyName) OrElse String.Equals(e.PropertyName, NameOf(ICaptureSessionPageViewModel.IsLocked), StringComparison.Ordinal) Then
                _openNavigationMenuCommand.RaiseCanExecuteChanged()
                _navigateToPageCommand.RaiseCanExecuteChanged()
            End If
        End Sub

        Private Sub RegisterPage(page As AppPageDescriptor)
            If page Is Nothing Then
                Return
            End If

            _pages.Add(page)
        End Sub

        Private Sub RefreshPageLocalization()
            Dim currentCulture = _localization.CurrentCulture

            For index = 0 To _pages.Count - 1
                Dim page = _pages(index)
                Dim title = ResolveText(page.TitleResourceKey, page.FallbackTitle)
                Dim summary = ResolveText(page.SummaryResourceKey, page.FallbackSummary)
                Dim menuMeta = ResolveText(page.MenuMetaResourceKey, page.FallbackMenuMeta)
                Dim menuTitle = currentCulture.TextInfo.ToUpper(title)

                page.SetDisplayText(title, summary)
                page.SetMenuDisplayText(menuTitle, menuMeta, $"{index + 1:00}.")
            Next
        End Sub

        Private Sub UpdateLanguageToggleText()
            Dim nextLanguage = GetNextLanguage()
            CurrentLanguageKeyText = ResolveLanguageKeyText(_localization.CurrentLanguage)

            If nextLanguage Is Nothing Then
                LanguageToggleText = String.Empty
                LanguageToggleKeyText = String.Empty
                CurrentLanguageKeyText = String.Empty
                Return
            End If

            If nextLanguage.CultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase) Then
                LanguageToggleText = L("Language.Toggle.ToChinese")
                LanguageToggleKeyText = ResolveLanguageKeyText(nextLanguage)
                Return
            End If

            If nextLanguage.CultureName.StartsWith("en", StringComparison.OrdinalIgnoreCase) Then
                LanguageToggleText = L("Language.Toggle.ToEnglish")
                LanguageToggleKeyText = ResolveLanguageKeyText(nextLanguage)
                Return
            End If

            LanguageToggleText = nextLanguage.NativeName
            LanguageToggleKeyText = ResolveLanguageKeyText(nextLanguage)
        End Sub

        Private Function GetNextLanguage() As LanguageOption
            Dim availableLanguages = _localization.AvailableLanguages
            If availableLanguages Is Nothing OrElse availableLanguages.Count = 0 Then
                Return Nothing
            End If

            Dim currentLanguage = _localization.CurrentLanguage
            If currentLanguage Is Nothing Then
                Return availableLanguages(0)
            End If

            Dim currentIndex = -1
            For index = 0 To availableLanguages.Count - 1
                If String.Equals(availableLanguages(index).CultureName, currentLanguage.CultureName, StringComparison.OrdinalIgnoreCase) Then
                    currentIndex = index
                    Exit For
                End If
            Next

            If currentIndex < 0 Then
                Return availableLanguages(0)
            End If

            Return availableLanguages((currentIndex + 1) Mod availableLanguages.Count)
        End Function

        Private Sub UpdateThemeToggleText()
            Dim nextTheme = GetNextTheme()
            CurrentThemeKeyText = If(_themeManager.CurrentTheme = AppTheme.Light,
                                     L("Theme.Toggle.KeyLight"),
                                     L("Theme.Toggle.KeyDark"))

            If nextTheme = AppTheme.Light Then
                ThemeToggleText = L("Theme.Toggle.ToLight")
                ThemeToggleKeyText = L("Theme.Toggle.KeyLight")
                Return
            End If

            ThemeToggleText = L("Theme.Toggle.ToDark")
            ThemeToggleKeyText = L("Theme.Toggle.KeyDark")
        End Sub

        Private Function GetNextTheme() As AppTheme
            If _themeManager.CurrentTheme = AppTheme.Dark Then
                Return AppTheme.Light
            End If

            Return AppTheme.Dark
        End Function

        Private Function L(key As String, ParamArray args() As Object) As String
            Return _localization.GetString(key, args)
        End Function

        Private Shared Function ResolveLanguageKeyText(language As LanguageOption) As String
            If language Is Nothing OrElse String.IsNullOrWhiteSpace(language.CultureName) Then
                Return String.Empty
            End If

            Return language.CultureName.Split("-"c)(0).ToUpperInvariant()
        End Function

        Private Function ResolveText(resourceKey As String, fallback As String) As String
            If String.IsNullOrWhiteSpace(resourceKey) Then
                Return fallback
            End If

            Dim localized = _localization.GetString(resourceKey)
            If String.Equals(localized, resourceKey, StringComparison.Ordinal) Then
                Return fallback
            End If

            Return localized
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then
                Return
            End If

            _disposed = True

            DetachCurrentPageNotifications()

            RemoveHandler _localization.LanguageChanged, AddressOf OnLanguageChanged
            RemoveHandler _themeManager.ThemeChanged, AddressOf OnThemeChanged

            For Each page In _pages
                Dim disposablePage = TryCast(page.PageViewModel, IDisposable)
                If disposablePage IsNot Nothing Then
                    disposablePage.Dispose()
                End If
            Next

            _rawInputBroker.Dispose()
        End Sub
    End Class
End Namespace
