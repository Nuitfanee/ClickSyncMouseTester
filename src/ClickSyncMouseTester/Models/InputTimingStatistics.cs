namespace ClickSyncMouseTester.Models;

public class InputTimingStatistics
{
    private int _downCount;

    private int _upCount;

    private int _doubleClickCount;

    private double _lastDownTimestampMs;

    private double _downStartTimestampMs;

    private double? _currentDownDownMs;

    private double? _minimumDownDownMs;

    private double? _currentDownUpMs;

    private double? _minimumDownUpMs;

    private double _downDownSum;

    private int _downDownSampleCount;

    private double _downUpSum;

    private int _downUpSampleCount;

    private bool _hasPreviousDown;

    private bool _isPressed;

    public int DownCount => _downCount;

    public int UpCount => _upCount;

    public int DoubleClickCount => _doubleClickCount;

    public double? CurrentDownDownMs => _currentDownDownMs;

    public double? MinimumDownDownMs => _minimumDownDownMs;

    public double? AverageDownDownMs => _downDownSampleCount > 0 ? _downDownSum / (double)_downDownSampleCount : null;

    public double? CurrentDownUpMs => _currentDownUpMs;

    public double? MinimumDownUpMs => _minimumDownUpMs;

    public double? AverageDownUpMs => _downUpSampleCount > 0 ? _downUpSum / (double)_downUpSampleCount : null;

    public bool IsPressed => _isPressed;

    public InputTimingStatistics()
    {
        Reset();
    }

    public bool RegisterDown(double timestampMs, double doubleClickThresholdMs)
    {
        if (_isPressed)
        {
            return false;
        }

        bool isDoubleClick = false;
        _isPressed = true;
        _downStartTimestampMs = timestampMs;
        if (_hasPreviousDown)
        {
            double downDownIntervalMs = timestampMs - _lastDownTimestampMs;
            _currentDownDownMs = downDownIntervalMs;
            if (downDownIntervalMs > 0.2 && downDownIntervalMs < 3000.0)
            {
                if (!_minimumDownDownMs.HasValue || downDownIntervalMs < _minimumDownDownMs.Value)
                {
                    _minimumDownDownMs = downDownIntervalMs;
                }
                _downDownSum += downDownIntervalMs;
                _downDownSampleCount++;
            }
            if (downDownIntervalMs > 0.0 && downDownIntervalMs < doubleClickThresholdMs)
            {
                _doubleClickCount++;
                isDoubleClick = true;
            }
        }
        else
        {
            _currentDownDownMs = null;
        }
        _downCount++;
        _lastDownTimestampMs = timestampMs;
        _hasPreviousDown = true;
        return isDoubleClick;
    }

    public void RegisterUp(double timestampMs)
    {
        if (_isPressed && !double.IsNaN(_downStartTimestampMs))
        {
            double downUpIntervalMs = timestampMs - _downStartTimestampMs;
            _currentDownUpMs = downUpIntervalMs;
            if (downUpIntervalMs > 0.2 && downUpIntervalMs < 5000.0)
            {
                if (!_minimumDownUpMs.HasValue || downUpIntervalMs < _minimumDownUpMs.Value)
                {
                    _minimumDownUpMs = downUpIntervalMs;
                }
                _downUpSum += downUpIntervalMs;
                _downUpSampleCount++;
            }
        }
        _isPressed = false;
        _downStartTimestampMs = double.NaN;
        _upCount++;
    }

    public void Reset()
    {
        _downCount = 0;
        _upCount = 0;
        _doubleClickCount = 0;
        _lastDownTimestampMs = double.NaN;
        _downStartTimestampMs = double.NaN;
        _currentDownDownMs = null;
        _minimumDownDownMs = null;
        _currentDownUpMs = null;
        _minimumDownUpMs = null;
        _downDownSum = 0.0;
        _downDownSampleCount = 0;
        _downUpSum = 0.0;
        _downUpSampleCount = 0;
        _hasPreviousDown = false;
        _isPressed = false;
    }

    public void ResetPressedState()
    {
        _isPressed = false;
        _downStartTimestampMs = double.NaN;
    }
}





