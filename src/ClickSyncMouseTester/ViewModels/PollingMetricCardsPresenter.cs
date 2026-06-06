using ClickSyncMouseTester.Infrastructure;
using ClickSyncMouseTester.Models;
using System;
using System.Collections.ObjectModel;

namespace ClickSyncMouseTester.ViewModels;

public sealed class PollingMetricCardsPresenter : BindableBase
{
    private const string EmptyValueText = "--";
    private const string HertzUnitText = "Hz";

    private readonly PollingMetricCardViewModel _peakRateCard;
    private readonly PollingMetricCardViewModel _emptyPacketRateCard;
    private readonly PollingMetricCardViewModel _droppedPacketCountCard;

    private PollingRateMode _mode;

    public ObservableCollection<PollingMetricCardViewModel> Cards { get; }

    public int ColumnCount => Cards.Count > 0 ? Cards.Count : 1;

    public PollingMetricCardsPresenter()
    {
        _peakRateCard = new PollingMetricCardViewModel();
        _emptyPacketRateCard = new PollingMetricCardViewModel();
        _droppedPacketCountCard = new PollingMetricCardViewModel();
        Cards = new ObservableCollection<PollingMetricCardViewModel>();
        _mode = PollingRateMode.MotionReportRate;
        RebuildCards();
    }

    public void RefreshTitles(Func<string, string> localize)
    {
        if (localize == null)
        {
            throw new ArgumentNullException(nameof(localize));
        }

        _peakRateCard.TitleText = localize("Metrics.Peak");
        _emptyPacketRateCard.TitleText = localize("Metrics.EmptyPacketRate");
        _droppedPacketCountCard.TitleText = localize("Metrics.DropCount");
    }

    public void SetMode(PollingRateMode mode)
    {
        if (_mode == mode && Cards.Count > 0)
        {
            return;
        }

        _mode = mode;
        RebuildCards();
    }

    public void UpdateValues(string peakRateText, string emptyPacketRateText, string droppedPacketCountText)
    {
        _peakRateCard.ValueText = FormatMetricWithUnit(peakRateText, HertzUnitText);
        _emptyPacketRateCard.ValueText = NormalizeMetricText(emptyPacketRateText);
        _droppedPacketCountCard.ValueText = NormalizeMetricText(droppedPacketCountText);
    }

    private void RebuildCards()
    {
        int previousColumnCount = ColumnCount;

        Cards.Clear();
        Cards.Add(_peakRateCard);
        if (_mode == PollingRateMode.MotionReportRate)
        {
            Cards.Add(_emptyPacketRateCard);
        }

        Cards.Add(_droppedPacketCountCard);

        if (previousColumnCount != ColumnCount)
        {
            RaisePropertyChanged(nameof(ColumnCount));
        }
    }

    private static string FormatMetricWithUnit(string value, string unit)
    {
        string metricText = NormalizeMetricText(value);
        if (string.Equals(metricText, EmptyValueText, StringComparison.Ordinal))
        {
            return EmptyValueText;
        }

        return metricText + " " + unit;
    }

    private static string NormalizeMetricText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? EmptyValueText : value;
    }
}
