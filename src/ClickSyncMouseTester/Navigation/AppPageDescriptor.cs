using ClickSyncMouseTester.Infrastructure;

namespace ClickSyncMouseTester.Navigation;

public sealed class AppPageDescriptor : BindableBase
{
    private string _displayTitle;

    private string _displaySummary;

    private string _displayMenuTitle;

    private string _displayMenuMeta;

    private string _menuIndexText;

    private bool _isCurrentPage;

    public AppPageKey PageKey { get; }

    public string TitleResourceKey { get; }

    public string FallbackTitle { get; }

    public string SummaryResourceKey { get; }

    public string FallbackSummary { get; }

    public string MenuMetaResourceKey { get; }

    public string FallbackMenuMeta { get; }

    public object PageViewModel { get; }

    public string DisplayTitle
    {
        get
        {
            return _displayTitle;
        }
        private set
        {
            SetProperty(ref _displayTitle, value, "DisplayTitle");
        }
    }

    public string DisplaySummary
    {
        get
        {
            return _displaySummary;
        }
        private set
        {
            SetProperty(ref _displaySummary, value, "DisplaySummary");
        }
    }

    public string DisplayMenuTitle
    {
        get
        {
            return _displayMenuTitle;
        }
        private set
        {
            SetProperty(ref _displayMenuTitle, value, "DisplayMenuTitle");
        }
    }

    public string DisplayMenuMeta
    {
        get
        {
            return _displayMenuMeta;
        }
        private set
        {
            SetProperty(ref _displayMenuMeta, value, "DisplayMenuMeta");
        }
    }

    public string MenuIndexText
    {
        get
        {
            return _menuIndexText;
        }
        private set
        {
            SetProperty(ref _menuIndexText, value, "MenuIndexText");
        }
    }

    public bool IsCurrentPage
    {
        get
        {
            return _isCurrentPage;
        }
        private set
        {
            SetProperty(ref _isCurrentPage, value, "IsCurrentPage");
        }
    }

    public AppPageDescriptor(AppPageKey pageKey, string titleResourceKey, string fallbackTitle, string summaryResourceKey, string fallbackSummary, string menuMetaResourceKey, string fallbackMenuMeta, object pageViewModel)
    {
        PageKey = pageKey;
        TitleResourceKey = titleResourceKey;
        FallbackTitle = fallbackTitle;
        SummaryResourceKey = summaryResourceKey;
        FallbackSummary = fallbackSummary;
        MenuMetaResourceKey = menuMetaResourceKey;
        FallbackMenuMeta = fallbackMenuMeta;
        PageViewModel = pageViewModel;
        _displayTitle = fallbackTitle;
        _displaySummary = fallbackSummary;
        _displayMenuTitle = fallbackTitle;
        _displayMenuMeta = fallbackMenuMeta;
        _menuIndexText = string.Empty;
    }

    public void SetDisplayText(string title, string summary)
    {
        DisplayTitle = title;
        DisplaySummary = summary;
    }

    public void SetMenuDisplayText(string menuTitle, string menuMeta, string menuIndexText)
    {
        DisplayMenuTitle = menuTitle;
        DisplayMenuMeta = menuMeta;
        MenuIndexText = menuIndexText;
    }

    public void SetCurrentPageState(bool isCurrentPage)
    {
        IsCurrentPage = isCurrentPage;
    }
}






