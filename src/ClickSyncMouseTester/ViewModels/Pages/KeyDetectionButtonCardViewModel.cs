using ClickSyncMouseTester.Infrastructure;
using ClickSyncMouseTester.Models;
using ClickSyncMouseTester.Services;
using System;
using System.Runtime.Versioning;
using System.Windows.Threading;

namespace ClickSyncMouseTester.ViewModels.Pages;

[SupportedOSPlatform("windows")]
public class KeyDetectionButtonCardViewModel : BindableBase, IDisposable
{
    private const double DoublePulseDurationMilliseconds = 350.0;

    private readonly MouseButtonKind _buttonKind;

    private readonly InputTimingStatistics _stats;

    private readonly DispatcherTimer _doublePulseTimer;

    private string _title;

    private string _doubleClickCountText;

    private string _downCountText;

    private string _upCountText;

    private string _currentDownDownText;

    private string _minimumDownDownText;

    private string _averageDownDownText;

    private string _currentDownUpText;

    private string _minimumDownUpText;

    private string _averageDownUpText;

    private bool _isPressed;

    private bool _isDoublePulseActive;

    private bool _disposed;

    public MouseButtonKind ButtonKind => _buttonKind;

    public string Title
    {
        get
        {
            return _title;
        }
        private set
        {
            SetProperty(ref _title, value, "Title");
        }
    }

    public string DoubleClickCountText
    {
        get
        {
            return _doubleClickCountText;
        }
        private set
        {
            SetProperty(ref _doubleClickCountText, value, "DoubleClickCountText");
        }
    }

    public string DownCountText
    {
        get
        {
            return _downCountText;
        }
        private set
        {
            SetProperty(ref _downCountText, value, "DownCountText");
        }
    }

    public string UpCountText
    {
        get
        {
            return _upCountText;
        }
        private set
        {
            SetProperty(ref _upCountText, value, "UpCountText");
        }
    }

    public string CurrentDownDownText
    {
        get
        {
            return _currentDownDownText;
        }
        private set
        {
            SetProperty(ref _currentDownDownText, value, "CurrentDownDownText");
        }
    }

    public string MinimumDownDownText
    {
        get
        {
            return _minimumDownDownText;
        }
        private set
        {
            SetProperty(ref _minimumDownDownText, value, "MinimumDownDownText");
        }
    }

    public string AverageDownDownText
    {
        get
        {
            return _averageDownDownText;
        }
        private set
        {
            SetProperty(ref _averageDownDownText, value, "AverageDownDownText");
        }
    }

    public string CurrentDownUpText
    {
        get
        {
            return _currentDownUpText;
        }
        private set
        {
            SetProperty(ref _currentDownUpText, value, "CurrentDownUpText");
        }
    }

    public string MinimumDownUpText
    {
        get
        {
            return _minimumDownUpText;
        }
        private set
        {
            SetProperty(ref _minimumDownUpText, value, "MinimumDownUpText");
        }
    }

    public string AverageDownUpText
    {
        get
        {
            return _averageDownUpText;
        }
        private set
        {
            SetProperty(ref _averageDownUpText, value, "AverageDownUpText");
        }
    }

    public bool IsPressed
    {
        get
        {
            return _isPressed;
        }
        private set
        {
            SetProperty(ref _isPressed, value, "IsPressed");
        }
    }

    public bool IsDoublePulseActive
    {
        get
        {
            return _isDoublePulseActive;
        }
        private set
        {
            SetProperty(ref _isDoublePulseActive, value, "IsDoublePulseActive");
        }
    }

    public KeyDetectionButtonCardViewModel(MouseButtonKind buttonKind)
    {
        _buttonKind = buttonKind;
        _stats = new InputTimingStatistics();
        _doublePulseTimer = new DispatcherTimer();
        _doublePulseTimer.Interval = TimeSpan.FromMilliseconds(350.0);
        _doublePulseTimer.Tick += OnDoublePulseTimerTick;
        SyncDisplay();
    }

    public void RefreshLocalization(LocalizationManager localization)
    {
        if (localization != null)
        {
            Title = localization.GetString(ResolveTitleKey());
        }
    }

    public void RegisterDown(double timestampMs, double doubleClickThresholdMs)
    {
        bool isDoubleClick = _stats.RegisterDown(timestampMs, doubleClickThresholdMs);
        SyncDisplay();
        if (isDoubleClick)
        {
            PulseDoubleHighlight();
        }
    }

    public void RegisterUp(double timestampMs)
    {
        _stats.RegisterUp(timestampMs);
        SyncDisplay();
    }

    public void ResetStatistics()
    {
        _stats.Reset();
        _doublePulseTimer.Stop();
        IsDoublePulseActive = false;
        SyncDisplay();
    }

    public void ResetPressedState()
    {
        _stats.ResetPressedState();
        SyncDisplay();
    }

    private void SyncDisplay()
    {
        DoubleClickCountText = KeyDetectionFormatting.FormatCount(_stats.DoubleClickCount);
        DownCountText = KeyDetectionFormatting.FormatCount(_stats.DownCount);
        UpCountText = KeyDetectionFormatting.FormatCount(_stats.UpCount);
        CurrentDownDownText = KeyDetectionFormatting.FormatMilliseconds(_stats.CurrentDownDownMs);
        MinimumDownDownText = KeyDetectionFormatting.FormatMilliseconds(_stats.MinimumDownDownMs);
        AverageDownDownText = KeyDetectionFormatting.FormatMilliseconds(_stats.AverageDownDownMs);
        CurrentDownUpText = KeyDetectionFormatting.FormatMilliseconds(_stats.CurrentDownUpMs);
        MinimumDownUpText = KeyDetectionFormatting.FormatMilliseconds(_stats.MinimumDownUpMs);
        AverageDownUpText = KeyDetectionFormatting.FormatMilliseconds(_stats.AverageDownUpMs);
        IsPressed = _stats.IsPressed;
    }

    private string ResolveTitleKey()
    {
        return ButtonKind switch
        {
            MouseButtonKind.LeftButton => "KeyDetection.Mouse.Left",
            MouseButtonKind.MiddleButton => "KeyDetection.Mouse.Middle",
            MouseButtonKind.RightButton => "KeyDetection.Mouse.Right",
            MouseButtonKind.ForwardButton => "KeyDetection.Mouse.Forward",
            _ => "KeyDetection.Mouse.Back",
        };
    }

    private void PulseDoubleHighlight()
    {
        _doublePulseTimer.Stop();
        IsDoublePulseActive = true;
        _doublePulseTimer.Start();
    }

    private void OnDoublePulseTimerTick(object sender, EventArgs e)
    {
        _doublePulseTimer.Stop();
        IsDoublePulseActive = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _doublePulseTimer.Stop();
            _doublePulseTimer.Tick -= OnDoublePulseTimerTick;
        }
    }

    void IDisposable.Dispose()
    {
        this.Dispose();
    }
}





