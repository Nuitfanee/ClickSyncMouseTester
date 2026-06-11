using System;
using System.Collections.Generic;

namespace ClickSyncMouseTester.Models;

public class PollingChartRenderFrame
{
    private readonly double _rawCurrentRate;

    private readonly IReadOnlyList<PollingHistoryPoint> _historyPoints;

    public double RawCurrentRate => _rawCurrentRate;

    public IReadOnlyList<PollingHistoryPoint> HistoryPoints => _historyPoints;

    public PollingChartRenderFrame(double rawCurrentRate, IReadOnlyList<PollingHistoryPoint> historyPoints)
    {
        _rawCurrentRate = rawCurrentRate;
        _historyPoints = historyPoints ?? Array.Empty<PollingHistoryPoint>();
    }
}





