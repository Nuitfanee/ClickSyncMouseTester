using ClickSyncMouseTester.Infrastructure;
using System.Windows;

namespace ClickSyncMouseTester.ViewModels.Pages;

public class SensitivityMatchInfoBandViewModel : BindableBase
{
    private string _labelText;

    private string _deviceTitle;

    private string _bindingStateText;

    private string _metaText;

    private HorizontalAlignment _bandHorizontalAlignment;

    private double _bandScaleX;

    private GridLength _gapAfterTitle;

    private GridLength _gapAfterBindingState;

    public string LabelText
    {
        get
        {
            return _labelText;
        }
        set
        {
            SetProperty(ref _labelText, value, "LabelText");
        }
    }

    public string DeviceTitle
    {
        get
        {
            return _deviceTitle;
        }
        set
        {
            SetProperty(ref _deviceTitle, value, "DeviceTitle");
        }
    }

    public string BindingStateText
    {
        get
        {
            return _bindingStateText;
        }
        set
        {
            if (SetProperty(ref _bindingStateText, value, "BindingStateText"))
            {
                RefreshLayoutMetrics();
            }
        }
    }

    public string MetaText
    {
        get
        {
            return _metaText;
        }
        set
        {
            if (SetProperty(ref _metaText, value, "MetaText"))
            {
                RefreshLayoutMetrics();
            }
        }
    }

    public double BandScaleX
    {
        get
        {
            return _bandScaleX;
        }
        set
        {
            if (SetProperty(ref _bandScaleX, value, "BandScaleX"))
            {
                RefreshLayoutMetrics();
            }
        }
    }

    public HorizontalAlignment BandHorizontalAlignment
    {
        get
        {
            return _bandHorizontalAlignment;
        }
        set
        {
            SetProperty(ref _bandHorizontalAlignment, value, "BandHorizontalAlignment");
        }
    }

    public GridLength GapAfterTitle
    {
        get
        {
            return _gapAfterTitle;
        }
        private set
        {
            SetProperty(ref _gapAfterTitle, value, "GapAfterTitle");
        }
    }

    public GridLength GapAfterBindingState
    {
        get
        {
            return _gapAfterBindingState;
        }
        private set
        {
            SetProperty(ref _gapAfterBindingState, value, "GapAfterBindingState");
        }
    }

    private void RefreshLayoutMetrics()
    {
        bool hasBindingStateText = !string.IsNullOrWhiteSpace(_bindingStateText);
        bool hasMetaText = !string.IsNullOrWhiteSpace(_metaText);
        GapAfterTitle = (hasBindingStateText ? new GridLength(16.0) : new GridLength(0.0));
        GapAfterBindingState = (hasMetaText ? new GridLength(16.0) : new GridLength(0.0));
    }
}





