using ClickSyncMouseTester.Infrastructure;
using ClickSyncMouseTester.Controls.Brand;
using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Navigation;
using ClickSyncMouseTester.Services;
using ClickSyncMouseTester.ViewModels.Pages;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;

namespace ClickSyncMouseTester.ViewModels;

[SupportedOSPlatform("windows")]
public class ShellViewModel : BindableBase, IDisposable
{
    private readonly LocalizationManager _localization;

    private readonly ThemeManager _themeManager;

    private readonly NavigationBrandLogoService _navigationBrandLogoService;

    private readonly NavigationBrandDeviceResolver _navigationBrandDeviceResolver;

    private readonly DeviceSelectionCoordinator _deviceSelectionCoordinator;

    private readonly ObservableCollection<AppPageDescriptor> _pages;

    private readonly DelegateCommand _toggleLanguageCommand;

    private readonly DelegateCommand _toggleThemeCommand;

    private readonly DelegateCommand _openNavigationMenuCommand;

    private readonly DelegateCommand _closeNavigationMenuCommand;

    private readonly DelegateCommand _navigateToPageCommand;

    private readonly IRawInputBroker _rawInputBroker;

    private readonly MainWindowViewModel _pagePollingDashboard;

    private readonly MousePerformancePageViewModel _pageMousePerformance;

    private readonly KeyDetectionPageViewModel _pageKeyDetection;

    private readonly SensitivityMatchingPageViewModel _pageSensitivityMatching;

    private readonly AngleCalibrationPageViewModel _pageAngleCalibration;

    private string _languageToggleText;

    private string _languageToggleKeyText;

    private string _currentLanguageKeyText;

    private string _themeToggleText;

    private string _themeToggleKeyText;

    private string _currentThemeKeyText;

    private BrandLogoDefinition _navigationBrandLogoDefinition;

    private object _currentPage;

    private AppPageDescriptor _currentPageDescriptor;

    private bool _isNavigationMenuOpen;

    private bool _disposed;

    public ObservableCollection<AppPageDescriptor> Pages => _pages;

    public object CurrentPage
    {
        get
        {
            return _currentPage;
        }
        private set
        {
            SetProperty(ref _currentPage, value, "CurrentPage");
        }
    }

    public AppPageDescriptor CurrentPageDescriptor
    {
        get
        {
            return _currentPageDescriptor;
        }
        private set
        {
            SetProperty(ref _currentPageDescriptor, value, "CurrentPageDescriptor");
        }
    }

    public AppPageKey CurrentPageKey
    {
        get
        {
            if (_currentPageDescriptor == null)
            {
                return AppPageKey.PollingDashboard;
            }
            return _currentPageDescriptor.PageKey;
        }
    }

    public string CurrentPageTitle
    {
        get
        {
            if (_currentPageDescriptor == null)
            {
                return string.Empty;
            }
            return _currentPageDescriptor.DisplayTitle;
        }
    }

    public string CurrentPageSummary
    {
        get
        {
            if (_currentPageDescriptor == null)
            {
                return string.Empty;
            }
            return _currentPageDescriptor.DisplaySummary;
        }
    }

    public bool IsChineseLanguageCurrent => _localization.CurrentLanguage?.CultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ?? false;

    public bool IsEnglishLanguageCurrent => _localization.CurrentLanguage?.CultureName.StartsWith("en", StringComparison.OrdinalIgnoreCase) ?? false;

    public bool IsLightThemeCurrent => _themeManager.CurrentTheme == AppTheme.Light;

    public bool IsDarkThemeCurrent => _themeManager.CurrentTheme == AppTheme.Dark;

    public bool IsNavigationMenuOpen
    {
        get
        {
            return _isNavigationMenuOpen;
        }
        private set
        {
            if (SetProperty(ref _isNavigationMenuOpen, value, "IsNavigationMenuOpen"))
            {
                _openNavigationMenuCommand.RaiseCanExecuteChanged();
                _closeNavigationMenuCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LanguageToggleText
    {
        get
        {
            return _languageToggleText;
        }
        private set
        {
            SetProperty(ref _languageToggleText, value, "LanguageToggleText");
        }
    }

    public string LanguageToggleKeyText
    {
        get
        {
            return _languageToggleKeyText;
        }
        private set
        {
            SetProperty(ref _languageToggleKeyText, value, "LanguageToggleKeyText");
        }
    }

    public string CurrentLanguageKeyText
    {
        get
        {
            return _currentLanguageKeyText;
        }
        private set
        {
            SetProperty(ref _currentLanguageKeyText, value, "CurrentLanguageKeyText");
        }
    }

    public string ThemeToggleText
    {
        get
        {
            return _themeToggleText;
        }
        private set
        {
            SetProperty(ref _themeToggleText, value, "ThemeToggleText");
        }
    }

    public string ThemeToggleKeyText
    {
        get
        {
            return _themeToggleKeyText;
        }
        private set
        {
            SetProperty(ref _themeToggleKeyText, value, "ThemeToggleKeyText");
        }
    }

    public string CurrentThemeKeyText
    {
        get
        {
            return _currentThemeKeyText;
        }
        private set
        {
            SetProperty(ref _currentThemeKeyText, value, "CurrentThemeKeyText");
        }
    }

    public BrandLogoDefinition NavigationBrandLogoDefinition
    {
        get
        {
            return _navigationBrandLogoDefinition;
        }
        private set
        {
            SetProperty(ref _navigationBrandLogoDefinition, value, "NavigationBrandLogoDefinition");
        }
    }

    public DelegateCommand ToggleLanguageCommand => _toggleLanguageCommand;

    public DelegateCommand ToggleThemeCommand => _toggleThemeCommand;

    public DelegateCommand OpenNavigationMenuCommand => _openNavigationMenuCommand;

    public DelegateCommand CloseNavigationMenuCommand => _closeNavigationMenuCommand;

    public DelegateCommand NavigateToPageCommand => _navigateToPageCommand;

    public ICaptureSessionPageViewModel ActiveCapturePage => CurrentPage as ICaptureSessionPageViewModel;

    public ICaptureKeyboardShortcutHandler ActiveKeyboardShortcutPage => CurrentPage as ICaptureKeyboardShortcutHandler;

    public event EventHandler CurrentPageChanged;

    public ShellViewModel()
    {
        _localization = LocalizationManager.Instance;
        _themeManager = ThemeManager.Instance;
        _navigationBrandLogoService = new NavigationBrandLogoService();
        _localization.Initialize();
        _toggleLanguageCommand = new DelegateCommand(RequestToggleLanguage);
        _toggleThemeCommand = new DelegateCommand(RequestToggleTheme);
        _openNavigationMenuCommand = new DelegateCommand(RequestOpenNavigationMenu, CanOpenNavigationMenu);
        _closeNavigationMenuCommand = new DelegateCommand(RequestCloseNavigationMenu, CanCloseNavigationMenu);
        _navigateToPageCommand = new DelegateCommand(RequestNavigateToPage, CanNavigateToPage);
        _rawInputBroker = new RawInputBroker();
        _deviceSelectionCoordinator = new DeviceSelectionCoordinator();
        _navigationBrandDeviceResolver = new NavigationBrandDeviceResolver(_rawInputBroker, _deviceSelectionCoordinator);
        _pages = new ObservableCollection<AppPageDescriptor>();
        _pagePollingDashboard = new MainWindowViewModel(_rawInputBroker, _deviceSelectionCoordinator);
        _pageMousePerformance = new MousePerformancePageViewModel(_rawInputBroker, null, _deviceSelectionCoordinator);
        _pageKeyDetection = new KeyDetectionPageViewModel(_rawInputBroker);
        _pageSensitivityMatching = new SensitivityMatchingPageViewModel(_rawInputBroker);
        _pageAngleCalibration = new AngleCalibrationPageViewModel(_rawInputBroker);
        RegisterPage(new AppPageDescriptor(AppPageKey.PollingDashboard, "Navigation.Page.PollingDashboard", "Report Rate", "Navigation.Page.PollingDashboard.Summary", "The primary workspace with live Raw Input report-rate metrics, history trace, and capture controls.", "Navigation.Page.PollingDashboard.MenuMeta", "RAW INPUT REPORT MONITOR", _pagePollingDashboard));
        RegisterPage(new AppPageDescriptor(AppPageKey.MousePerformance, "Navigation.Page.MousePerformance", "Mouse Performance", "Navigation.Page.MousePerformance.Summary", "Lock onto one device, collect Raw Input packets, and inspect counts, path, and live plots.", "Navigation.Page.MousePerformance.MenuMeta", "MOUSE PERFORMANCE ANALYSIS", _pageMousePerformance));
        RegisterPage(new AppPageDescriptor(AppPageKey.KeyDetection, "Navigation.Page.KeyDetection", "Key Detection", "Navigation.Page.KeyDetection.Summary", "Inspect live mouse buttons, wheel activity, and a custom key with timing, double-click thresholds, and state changes.", "Navigation.Page.KeyDetection.MenuMeta", "BUTTON / WHEEL / KEY TIMING", _pageKeyDetection));
        RegisterPage(new AppPageDescriptor(AppPageKey.SensitivityMatching, "Navigation.Page.SensitivityMatching", "Sensitivity Match", "Navigation.Page.SensitivityMatching.Summary", "Bind two mice, complete three Raw Input rounds, and generate a recommended target DPI and scale.", "Navigation.Page.SensitivityMatching.MenuMeta", "MATCH TWO MOUSE FEELS", _pageSensitivityMatching));
        RegisterPage(new AppPageDescriptor(AppPageKey.AngleCalibration, "Navigation.Page.AngleCalibration", "Angle Calibration", "Navigation.Page.AngleCalibration.Summary", "Measure horizontal swipe traces, generate a recommended angle, and inspect stability and trace quality.", "Navigation.Page.AngleCalibration.MenuMeta", "MEASURE SWIPE ANGLE", _pageAngleCalibration));
        _pages.Move(3, 1);
        _pages.Move(4, 3);
        _localization.LanguageChanged += OnLanguageChanged;
        _themeManager.ThemeChanged += OnThemeChanged;
        RefreshPageLocalization();
        SwitchCurrentPage(ResolvePageDescriptor(AppPageKey.PollingDashboard), raiseChanged: false);
        UpdateLanguageToggleText();
        UpdateThemeToggleText();
    }

    public void OpenNavigationMenu()
    {
        if (CanOpenNavigationMenu())
        {
            RefreshNavigationBrandLogoDefinition();
            IsNavigationMenuOpen = true;
        }
    }

    public void CloseNavigationMenu()
    {
        if (IsNavigationMenuOpen)
        {
            IsNavigationMenuOpen = false;
        }
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        RefreshPageLocalization();
        UpdateLanguageToggleText();
        UpdateThemeToggleText();
        RaisePropertyChanged("IsChineseLanguageCurrent");
        RaisePropertyChanged("IsEnglishLanguageCurrent");
        RaisePropertyChanged("CurrentPageTitle");
        RaisePropertyChanged("CurrentPageSummary");
    }

    private void OnThemeChanged(object sender, EventArgs e)
    {
        UpdateThemeToggleText();
        RaisePropertyChanged("IsLightThemeCurrent");
        RaisePropertyChanged("IsDarkThemeCurrent");
    }

    private void RequestToggleLanguage()
    {
        LanguageOption nextLanguage = GetNextLanguage();
        if (nextLanguage != null)
        {
            _localization.SetLanguage(nextLanguage.CultureName);
        }
    }

    private void RequestToggleTheme()
    {
        _themeManager.ToggleTheme();
    }

    private void RequestOpenNavigationMenu()
    {
        OpenNavigationMenu();
    }

    private void RefreshNavigationBrandLogoDefinition()
    {
        RawMouseDeviceInfo device = _navigationBrandDeviceResolver.ResolvePreferredDevice();
        NavigationBrandLogoDefinition = _navigationBrandLogoService.ResolveLogoDefinition(device);
    }

    private bool CanOpenNavigationMenu()
    {
        if (!IsNavigationMenuOpen)
        {
            return !IsNavigationBlocked();
        }
        return false;
    }

    private void RequestCloseNavigationMenu()
    {
        CloseNavigationMenu();
    }

    private bool CanCloseNavigationMenu()
    {
        return IsNavigationMenuOpen;
    }

    private void RequestNavigateToPage(object parameter)
    {
        AppPageDescriptor appPageDescriptor = ResolvePageDescriptor(parameter);
        if (appPageDescriptor != null)
        {
            if (ReferenceEquals(appPageDescriptor, _currentPageDescriptor))
            {
                IsNavigationMenuOpen = false;
                return;
            }
            SwitchCurrentPage(appPageDescriptor, raiseChanged: true);
            IsNavigationMenuOpen = false;
        }
    }

    private bool CanNavigateToPage(object parameter)
    {
        if (ResolvePageDescriptor(parameter) != null)
        {
            return !IsNavigationBlocked();
        }
        return false;
    }

    private bool IsNavigationBlocked()
    {
        return ActiveCapturePage?.IsLocked ?? false;
    }

    private AppPageDescriptor ResolvePageDescriptor(object parameter)
    {
        if (parameter == null)
        {
            return null;
        }
        AppPageDescriptor pageDescriptor = parameter as AppPageDescriptor;
        if (pageDescriptor != null)
        {
            return _pages.FirstOrDefault(page => page.PageKey == pageDescriptor.PageKey);
        }
        if (parameter is AppPageKey)
        {
            AppPageKey pageKey = (AppPageKey)parameter;
            return _pages.FirstOrDefault(page => page.PageKey == pageKey);
        }
        if (Enum.TryParse<AppPageKey>(parameter.ToString(), ignoreCase: true, out var parsedPageKey))
        {
            return _pages.FirstOrDefault(page => page.PageKey == parsedPageKey);
        }
        return null;
    }

    private void SwitchCurrentPage(AppPageDescriptor targetPage, bool raiseChanged)
    {
        if (targetPage == null)
        {
            return;
        }
        if (ReferenceEquals(_currentPageDescriptor, targetPage))
        {
            if (raiseChanged)
            {
                CurrentPageChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        object? objectValue = CurrentPage;
        DetachCurrentPageNotifications();
        if (_currentPageDescriptor != null)
        {
            _currentPageDescriptor.SetCurrentPageState(isCurrentPage: false);
        }
        CurrentPageDescriptor = targetPage;
        CurrentPage = targetPage.PageViewModel;
        targetPage.SetCurrentPageState(isCurrentPage: true);
        ResetPageState(objectValue);
        AttachCurrentPageNotifications();
        RaisePropertyChanged("CurrentPageKey");
        RaisePropertyChanged("CurrentPageTitle");
        RaisePropertyChanged("CurrentPageSummary");
        _openNavigationMenuCommand.RaiseCanExecuteChanged();
        _closeNavigationMenuCommand.RaiseCanExecuteChanged();
        _navigateToPageCommand.RaiseCanExecuteChanged();
        if (raiseChanged)
        {
            CurrentPageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void ResetPageState(object pageViewModel)
    {
        if (pageViewModel is INavigationResettablePageViewModel navigationResettablePageViewModel)
        {
            navigationResettablePageViewModel.ResetToDefaultState();
        }
    }

    private void AttachCurrentPageNotifications()
    {
        if (CurrentPage is INotifyPropertyChanged notifyPropertyChanged)
        {
            notifyPropertyChanged.PropertyChanged += OnCurrentPagePropertyChanged;
        }
    }

    private void DetachCurrentPageNotifications()
    {
        if (CurrentPage is INotifyPropertyChanged notifyPropertyChanged)
        {
            notifyPropertyChanged.PropertyChanged -= OnCurrentPagePropertyChanged;
        }
    }

    private void OnCurrentPagePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || string.Equals(e.PropertyName, "IsLocked", StringComparison.Ordinal))
        {
            _openNavigationMenuCommand.RaiseCanExecuteChanged();
            _navigateToPageCommand.RaiseCanExecuteChanged();
        }
    }

    private void RegisterPage(AppPageDescriptor page)
    {
        if (page != null)
        {
            _pages.Add(page);
        }
    }

    private void RefreshPageLocalization()
    {
        CultureInfo currentCulture = _localization.CurrentCulture;
        for (int pageIndex = 0; pageIndex < _pages.Count; pageIndex++)
        {
            AppPageDescriptor pageDescriptor = _pages[pageIndex];
            string title = ResolveText(pageDescriptor.TitleResourceKey, pageDescriptor.FallbackTitle);
            string summary = ResolveText(pageDescriptor.SummaryResourceKey, pageDescriptor.FallbackSummary);
            string menuMeta = ResolveText(pageDescriptor.MenuMetaResourceKey, pageDescriptor.FallbackMenuMeta);
            string menuTitle = currentCulture.TextInfo.ToUpper(title);
            pageDescriptor.SetDisplayText(title, summary);
            pageDescriptor.SetMenuDisplayText(menuTitle, menuMeta, $"{pageIndex + 1:00}.");
        }
    }

    private void UpdateLanguageToggleText()
    {
        LanguageOption nextLanguage = GetNextLanguage();
        CurrentLanguageKeyText = ResolveLanguageKeyText(_localization.CurrentLanguage);
        if (nextLanguage == null)
        {
            LanguageToggleText = string.Empty;
            LanguageToggleKeyText = string.Empty;
            CurrentLanguageKeyText = string.Empty;
        }
        else if (nextLanguage.CultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            LanguageToggleText = L("Language.Toggle.ToChinese");
            LanguageToggleKeyText = ResolveLanguageKeyText(nextLanguage);
        }
        else if (nextLanguage.CultureName.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            LanguageToggleText = L("Language.Toggle.ToEnglish");
            LanguageToggleKeyText = ResolveLanguageKeyText(nextLanguage);
        }
        else
        {
            LanguageToggleText = nextLanguage.NativeName;
            LanguageToggleKeyText = ResolveLanguageKeyText(nextLanguage);
        }
    }

    private LanguageOption GetNextLanguage()
    {
        ReadOnlyCollection<LanguageOption> availableLanguages = _localization.AvailableLanguages;
        if (availableLanguages == null || availableLanguages.Count == 0)
        {
            return null;
        }
        LanguageOption currentLanguage = _localization.CurrentLanguage;
        if (currentLanguage == null)
        {
            return availableLanguages[0];
        }

        int currentLanguageIndex = -1;
        for (int languageIndex = 0; languageIndex < availableLanguages.Count; languageIndex++)
        {
            if (string.Equals(availableLanguages[languageIndex].CultureName, currentLanguage.CultureName, StringComparison.OrdinalIgnoreCase))
            {
                currentLanguageIndex = languageIndex;
                break;
            }
        }
        if (currentLanguageIndex < 0)
        {
            return availableLanguages[0];
        }
        return availableLanguages[(currentLanguageIndex + 1) % availableLanguages.Count];
    }

    private void UpdateThemeToggleText()
    {
        AppTheme nextTheme = GetNextTheme();
        CurrentThemeKeyText = ((_themeManager.CurrentTheme == AppTheme.Light) ? L("Theme.Toggle.KeyLight") : L("Theme.Toggle.KeyDark"));
        if (nextTheme == AppTheme.Light)
        {
            ThemeToggleText = L("Theme.Toggle.ToLight");
            ThemeToggleKeyText = L("Theme.Toggle.KeyLight");
        }
        else
        {
            ThemeToggleText = L("Theme.Toggle.ToDark");
            ThemeToggleKeyText = L("Theme.Toggle.KeyDark");
        }
    }

    private AppTheme GetNextTheme()
    {
        if (_themeManager.CurrentTheme == AppTheme.Dark)
        {
            return AppTheme.Light;
        }
        return AppTheme.Dark;
    }

    private string L(string key, params object[] args)
    {
        return _localization.GetString(key, args);
    }

    private static string ResolveLanguageKeyText(LanguageOption language)
    {
        if (language == null || string.IsNullOrWhiteSpace(language.CultureName))
        {
            return string.Empty;
        }
        return language.CultureName.Split('-')[0].ToUpperInvariant();
    }

    private string ResolveText(string resourceKey, string fallback)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return fallback;
        }
        string localizedText = _localization.GetString(resourceKey);
        if (string.Equals(localizedText, resourceKey, StringComparison.Ordinal))
        {
            return fallback;
        }
        return localizedText;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        DetachCurrentPageNotifications();
        _localization.LanguageChanged -= OnLanguageChanged;
        _themeManager.ThemeChanged -= OnThemeChanged;
        foreach (AppPageDescriptor page in _pages)
        {
            if (page.PageViewModel is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _rawInputBroker.Dispose();
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }
}
