using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClickSyncMouseTester.Services;

internal static class UiPerformanceProbe
{
    private const string PrimaryEnvironmentVariableName = "CLICKSYNCMOUSETESTER_UI_PERF";
    private const string ShortEnvironmentVariableName = "CLIKSYNC_UI_PERF";
    private const string FileEnvironmentVariableName = "CLICKSYNCMOUSETESTER_UI_PERF_FILE";

    private static readonly Stopwatch ProcessStopwatch = Stopwatch.StartNew();
    private static readonly bool IsEnabledValue = ResolveIsEnabled();
    private static readonly bool IsFileOutputEnabledValue = ResolveIsFileOutputEnabled();
    private static readonly UiPerformanceLogWriter LogWriter = IsFileOutputEnabledValue ? new UiPerformanceLogWriter() : null;
    private static UiPerformanceStageScope _startupToFirstFrameScope;

    public static bool IsEnabled => IsEnabledValue;

    public static IDisposable BeginStage(string stageName, Window owner = null)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(stageName))
        {
            return UiPerformanceNoopScope.Instance;
        }

        return new UiPerformanceStageScope(stageName, owner);
    }

    public static void StartStartupToFirstFrame(Window owner = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        UiPerformanceStageScope nextScope = new UiPerformanceStageScope("Startup.ToFirstFrame", owner);
        UiPerformanceStageScope previousScope = Interlocked.Exchange(ref _startupToFirstFrameScope, nextScope);
        previousScope?.Dispose();
    }

    public static void CompleteStartupToFirstFrame(Window owner = null)
    {
        UiPerformanceStageScope startupScope = Interlocked.Exchange(ref _startupToFirstFrameScope, null);
        if (startupScope == null)
        {
            return;
        }

        startupScope.AttachOwner(owner);
        startupScope.Dispose();
    }

    public static void Mark(string markerName, Window owner = null)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(markerName))
        {
            return;
        }

        double refreshRateHz = ResolveRefreshRateHz(owner);
        WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "UI PERF mark={0} processMs={1:F2} refreshHz={2:F2}",
            markerName,
            ProcessStopwatch.Elapsed.TotalMilliseconds,
            refreshRateHz));
    }

    private static bool ResolveIsEnabled()
    {
        return IsTruthy(Environment.GetEnvironmentVariable(PrimaryEnvironmentVariableName))
            || IsTruthy(Environment.GetEnvironmentVariable(ShortEnvironmentVariableName))
            || IsTruthy(AppContext.GetData(PrimaryEnvironmentVariableName) as string)
            || IsTruthy(AppContext.GetData(ShortEnvironmentVariableName) as string);
    }

    private static bool ResolveIsFileOutputEnabled()
    {
        return IsTruthy(Environment.GetEnvironmentVariable(FileEnvironmentVariableName))
            || IsTruthy(AppContext.GetData(FileEnvironmentVariableName) as string);
    }

    private static bool IsTruthy(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalizedValue = value.Trim();
        return string.Equals(normalizedValue, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedValue, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedValue, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedValue, "on", StringComparison.OrdinalIgnoreCase);
    }

    internal static double ResolveRefreshRateHz(Window owner)
    {
        if (owner == null)
        {
            return 60.0;
        }

        try
        {
            nint handle = new WindowInteropHelper(owner).Handle;
            double refreshRateHz = 0.0;
            if (NativeMethods.TryGetWindowDisplayRefreshRate(handle, ref refreshRateHz))
            {
                return refreshRateHz;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("UI PERF refresh-rate probe failed: " + ex.Message);
        }

        return 60.0;
    }

    internal static void WriteLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Trace.WriteLine(line);
        LogWriter?.Enqueue(line);
    }

    private sealed class UiPerformanceNoopScope : IDisposable
    {
        public static readonly UiPerformanceNoopScope Instance = new UiPerformanceNoopScope();

        private UiPerformanceNoopScope()
        {
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class UiPerformanceLogWriter
{
    private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
    private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "ui-performance.log");

    private int _isStarted;

    public void Enqueue(string line)
    {
        _lines.Enqueue(line);
        EnsureStarted();
        _signal.Release();
    }

    private void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _isStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            try
            {
                using StreamWriter writer = new StreamWriter(_logPath, append: true);
                while (_lines.TryDequeue(out string line))
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("UI PERF log write failed: " + ex.Message);
            }
        }
    }
}

internal sealed class UiPerformanceStageScope : IDisposable
{
    private readonly string _stageName;
    private readonly Stopwatch _stopwatch;
    private readonly int _gen0Count;
    private readonly int _gen1Count;
    private readonly int _gen2Count;
    private readonly long _allocatedBytes;
    private readonly UiFrameProbe _frameProbe;

    private Window _owner;
    private bool _disposed;

    public UiPerformanceStageScope(string stageName, Window owner)
    {
        _stageName = stageName;
        _owner = owner;
        _gen0Count = GC.CollectionCount(0);
        _gen1Count = GC.CollectionCount(1);
        _gen2Count = GC.CollectionCount(2);
        _allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        _frameProbe = new UiFrameProbe(UiPerformanceProbe.ResolveRefreshRateHz(owner));
        _stopwatch = Stopwatch.StartNew();
    }

    public void AttachOwner(Window owner)
    {
        if (_owner == null && owner != null)
        {
            _owner = owner;
            _frameProbe.SetRefreshRate(UiPerformanceProbe.ResolveRefreshRateHz(owner));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopwatch.Stop();
        UiFrameProbeSnapshot frameSnapshot = _frameProbe.DisposeAndSnapshot();
        long allocatedDelta = GC.GetTotalAllocatedBytes(precise: false) - _allocatedBytes;
        int gen0Delta = GC.CollectionCount(0) - _gen0Count;
        int gen1Delta = GC.CollectionCount(1) - _gen1Count;
        int gen2Delta = GC.CollectionCount(2) - _gen2Count;
        UiPerformanceProbe.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "UI PERF stage={0} elapsedMs={1:F2} frames={2} maxFrameMs={3:F2} longFrames={4} longFrameThresholdMs={5:F2} gc0={6} gc1={7} gc2={8} allocatedBytes={9}",
            _stageName,
            _stopwatch.Elapsed.TotalMilliseconds,
            frameSnapshot.FrameCount,
            frameSnapshot.MaxFrameMilliseconds,
            frameSnapshot.LongFrameCount,
            frameSnapshot.LongFrameThresholdMilliseconds,
            gen0Delta,
            gen1Delta,
            gen2Delta,
            allocatedDelta));
    }
}

internal sealed class UiFrameProbe
{
    private double _longFrameThresholdMilliseconds;
    private long _lastRenderingTicks;
    private double _maxFrameMilliseconds;
    private int _longFrameCount;
    private int _frameCount;
    private bool _isListening;

    public UiFrameProbe(double refreshRateHz)
    {
        SetRefreshRate(refreshRateHz);
        CompositionTarget.Rendering += OnRendering;
        _isListening = true;
    }

    public void SetRefreshRate(double refreshRateHz)
    {
        if (double.IsNaN(refreshRateHz) || double.IsInfinity(refreshRateHz) || refreshRateHz <= 1.0)
        {
            refreshRateHz = 60.0;
        }

        double expectedFrameMilliseconds = 1000.0 / refreshRateHz;
        _longFrameThresholdMilliseconds = Math.Max(24.0, expectedFrameMilliseconds * 1.5);
    }

    public UiFrameProbeSnapshot DisposeAndSnapshot()
    {
        if (_isListening)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isListening = false;
        }

        return new UiFrameProbeSnapshot(
            _frameCount,
            _maxFrameMilliseconds,
            _longFrameCount,
            _longFrameThresholdMilliseconds);
    }

    private void OnRendering(object sender, EventArgs e)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        if (_lastRenderingTicks > 0L)
        {
            double frameMilliseconds = (double)(nowTicks - _lastRenderingTicks) * 1000.0 / Stopwatch.Frequency;
            if (frameMilliseconds > _maxFrameMilliseconds)
            {
                _maxFrameMilliseconds = frameMilliseconds;
            }

            if (frameMilliseconds >= _longFrameThresholdMilliseconds)
            {
                _longFrameCount++;
            }
        }

        _lastRenderingTicks = nowTicks;
        _frameCount++;
    }
}

internal readonly struct UiFrameProbeSnapshot
{
    public UiFrameProbeSnapshot(int frameCount, double maxFrameMilliseconds, int longFrameCount, double longFrameThresholdMilliseconds)
    {
        FrameCount = frameCount;
        MaxFrameMilliseconds = maxFrameMilliseconds;
        LongFrameCount = longFrameCount;
        LongFrameThresholdMilliseconds = longFrameThresholdMilliseconds;
    }

    public int FrameCount { get; }

    public double MaxFrameMilliseconds { get; }

    public int LongFrameCount { get; }

    public double LongFrameThresholdMilliseconds { get; }
}
