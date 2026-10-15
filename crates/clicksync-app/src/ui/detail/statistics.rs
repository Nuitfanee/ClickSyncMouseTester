use crate::chart::labels::Unit;
use clicksync_chart::{
    Axis, Coordinate, Metric,
    analysis::detail::{
        DetailStatistics, HistogramStatistics, StatisticKind, StatisticUnit, StatisticValue,
    },
};

fn label(kind: StatisticKind, axis: Option<Axis>, english: bool) -> String {
    use StatisticKind as K;
    let (zh, en) = match kind {
        K::Samples => ("有效样本数", "Valid samples"),
        K::Mean => ("均值", "Mean"),
        K::StandardDeviation => ("标准差", "Std. deviation"),
        K::P05 => ("P5", "P5"),
        K::P95 => ("P95", "P95"),
        K::P99 => ("P99", "P99"),
        K::Maximum => ("最大值", "Maximum"),
        K::MeanAbsolute => ("平均绝对值", "Mean absolute"),
        K::PeakAbsolute => ("绝对峰值", "Absolute peak"),
        K::AbsoluteTravel => ("轴向总路程", "Axis travel"),
        K::Span => ("坐标跨度", "Coordinate span"),
        K::PathLength => ("累计路程", "Total travel"),
        K::Width => ("横向覆盖", "X coverage"),
        K::Height => ("纵向覆盖", "Y coverage"),
        K::ModalRange => ("最高柱区间", "Modal bin"),
        K::ModalShare => ("最高柱占比", "Modal share"),
    };
    let name = if english { en } else { zh };
    match axis {
        Some(axis) => format!("{} {name}", if axis == Axis::X { "X" } else { "Y" }),
        None => name.into(),
    }
}

fn value(value: Option<StatisticValue>, unit: StatisticUnit) -> String {
    let Some(value) = value else {
        return "—".into();
    };
    let (display, symbol) = match unit {
        StatisticUnit::Number => (None, ""),
        StatisticUnit::Counts => (Some(Unit::Count), "count"),
        StatisticUnit::Milliseconds => (Some(Unit::Milliseconds), "ms"),
        StatisticUnit::Hertz => (Some(Unit::Hertz), "Hz"),
        StatisticUnit::MetersPerSecond => (Some(Unit::MetersPerSecond), "m/s"),
        StatisticUnit::Percent => (Some(Unit::Percent), "%"),
    };
    let text = match value {
        StatisticValue::Integer(value) => value.to_string(),
        StatisticValue::Scalar(value) => display
            .unwrap_or(Unit::Count)
            .readout(Coordinate::Scalar(value)),
    };
    if symbol.is_empty() {
        text
    } else {
        format!("{text} {symbol}")
    }
}

pub(super) fn rows(
    statistics: &DetailStatistics,
    histogram: Option<HistogramStatistics>,
    metric: Metric,
    english: bool,
) -> Vec<(String, String)> {
    let mut rows: Vec<_> = statistics
        .rows
        .iter()
        .map(|row| {
            (
                label(row.kind, row.axis, english),
                value(row.value, row.unit),
            )
        })
        .collect();
    if let Some(histogram) = histogram {
        let unit = Unit::metric(metric);
        rows.push((
            label(StatisticKind::ModalRange, None, english),
            format!(
                "{} {}",
                unit.histogram_range(metric, histogram.modal_bin, histogram.last),
                unit.symbol()
            ),
        ));
        rows.push((
            label(StatisticKind::ModalShare, None, english),
            value(
                Some(StatisticValue::Scalar(histogram.modal_share)),
                StatisticUnit::Percent,
            ),
        ));
    }
    rows
}

pub(super) fn explanation(metric: Metric, histogram: bool, english: bool) -> Option<&'static str> {
    let (zh, en) = if histogram {
        (
            "按样本值统计，不使用柱高。",
            "Summarizes sample values, not bar heights.",
        )
    } else {
        match metric {
            Metric::Trajectory | Metric::PathLength => (
                "路程累加所选报告的位移。",
                "Travel sums selected report deltas.",
            ),
            Metric::Velocity(_) | Metric::VelocityMagnitude | Metric::PathSpeed => (
                "按滤波后的样本统计，非时长加权。",
                "Filtered samples; not time-weighted.",
            ),
            Metric::IntervalTrend | Metric::Frequency | Metric::CountTrend(_) => {
                ("统计平滑后的样本。", "Summarizes smoothed samples.")
            }
            Metric::RawFrequency => (
                "逐报告 Hz 的均值，并非整体报告率。",
                "Mean per-report Hz, not the overall report rate.",
            ),
            Metric::Count(_)
            | Metric::DeltaMagnitude
            | Metric::Cumulative(_)
            | Metric::Interval => {
                return None;
            }
        }
    };
    Some(if english { en } else { zh })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exact_integer_and_units_do_not_pass_through_float_conversion() {
        assert_eq!(
            value(
                Some(StatisticValue::Integer(9_007_199_254_740_993)),
                StatisticUnit::Counts
            ),
            "9007199254740993 count"
        );
        assert_eq!(
            value(
                Some(StatisticValue::Scalar(12.5)),
                StatisticUnit::MetersPerSecond
            ),
            "12.5 m/s"
        );
        assert_eq!(value(None, StatisticUnit::Percent), "—");
        assert_eq!(
            label(StatisticKind::PeakAbsolute, Some(Axis::Y), false),
            "Y 绝对峰值"
        );
        assert_eq!(label(StatisticKind::Mean, Some(Axis::X), true), "X Mean");
    }
}
