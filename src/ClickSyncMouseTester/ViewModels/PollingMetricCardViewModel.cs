using ClickSyncMouseTester.Infrastructure;

namespace ClickSyncMouseTester.ViewModels;

public sealed class PollingMetricCardViewModel : BindableBase
{
    private string _titleText;
    private string _valueText;

    public string TitleText
    {
        get
        {
            return _titleText;
        }
        set
        {
            SetProperty(ref _titleText, value, nameof(TitleText));
        }
    }

    public string ValueText
    {
        get
        {
            return _valueText;
        }
        set
        {
            SetProperty(ref _valueText, value, nameof(ValueText));
        }
    }

    public PollingMetricCardViewModel(string titleText = "", string valueText = "--")
    {
        _titleText = titleText;
        _valueText = valueText;
    }
}
