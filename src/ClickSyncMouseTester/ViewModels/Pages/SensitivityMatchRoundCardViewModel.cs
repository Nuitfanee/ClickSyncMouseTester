using ClickSyncMouseTester.Infrastructure;

namespace ClickSyncMouseTester.ViewModels.Pages;

public class SensitivityMatchRoundCardViewModel : BindableBase
{
    private string _title;

    private string _statusText;

    private string _valueText;

    private string _detailText;

    private bool _isCurrent;

    private bool _isCompleted;

    private bool _isFailed;

    private bool _showProgressTrack;

    private double _sourceTrackProgressValue;

    private double _targetTrackProgressValue;

    private double _trackProgressValue;

    private string _trackCaptionText;

    public string Title
    {
        get
        {
            return _title;
        }
        set
        {
            SetProperty(ref _title, value, "Title");
        }
    }

    public string StatusText
    {
        get
        {
            return _statusText;
        }
        set
        {
            SetProperty(ref _statusText, value, "StatusText");
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
            SetProperty(ref _valueText, value, "ValueText");
        }
    }

    public string DetailText
    {
        get
        {
            return _detailText;
        }
        set
        {
            SetProperty(ref _detailText, value, "DetailText");
        }
    }

    public bool IsCurrent
    {
        get
        {
            return _isCurrent;
        }
        set
        {
            SetProperty(ref _isCurrent, value, "IsCurrent");
        }
    }

    public bool IsCompleted
    {
        get
        {
            return _isCompleted;
        }
        set
        {
            SetProperty(ref _isCompleted, value, "IsCompleted");
        }
    }

    public bool IsFailed
    {
        get
        {
            return _isFailed;
        }
        set
        {
            SetProperty(ref _isFailed, value, "IsFailed");
        }
    }

    public bool ShowProgressTrack
    {
        get
        {
            return _showProgressTrack;
        }
        set
        {
            SetProperty(ref _showProgressTrack, value, "ShowProgressTrack");
        }
    }

    public double SourceTrackProgressValue
    {
        get
        {
            return _sourceTrackProgressValue;
        }
        set
        {
            SetProperty(ref _sourceTrackProgressValue, value, "SourceTrackProgressValue");
        }
    }

    public double TargetTrackProgressValue
    {
        get
        {
            return _targetTrackProgressValue;
        }
        set
        {
            SetProperty(ref _targetTrackProgressValue, value, "TargetTrackProgressValue");
        }
    }

    public double TrackProgressValue
    {
        get
        {
            return _trackProgressValue;
        }
        set
        {
            SetProperty(ref _trackProgressValue, value, "TrackProgressValue");
        }
    }

    public string TrackCaptionText
    {
        get
        {
            return _trackCaptionText;
        }
        set
        {
            SetProperty(ref _trackCaptionText, value, "TrackCaptionText");
        }
    }
}





