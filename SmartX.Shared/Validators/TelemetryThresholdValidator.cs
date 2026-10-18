
using System;
using SmartX.Shared.Models;

namespace SmartX.Shared.Validators;

public class TelemetryThresholdValidator
{
    public bool HasValidThresholdConfiguration(
        TelemetryMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        return
            double.IsFinite(metric.MinimumExpectedValue) &&
            double.IsFinite(metric.MaximumExpectedValue) &&
            double.IsFinite(metric.WarningThreshold) &&
            double.IsFinite(metric.CriticalThreshold) &&
            metric.MinimumExpectedValue <= metric.MaximumExpectedValue &&
            metric.WarningThreshold <= metric.CriticalThreshold;
    }

    public bool IsWithinExpectedRange(
        TelemetryMetric metric,
        double value)
    {
        ArgumentNullException.ThrowIfNull(metric);

        return double.IsFinite(value) &&
               value >= metric.MinimumExpectedValue &&
               value <= metric.MaximumExpectedValue;
    }

    public bool IsWarning(
        TelemetryMetric metric,
        double value)
    {
        ArgumentNullException.ThrowIfNull(metric);

        return double.IsFinite(value) &&
               value >= metric.WarningThreshold;
    }

    public bool IsCritical(
        TelemetryMetric metric,
        double value)
    {
        ArgumentNullException.ThrowIfNull(metric);

        return double.IsFinite(value) &&
               value >= metric.CriticalThreshold;
    }

    public bool IsAnomaly(
        TelemetryMetric metric,
        double value)
    {
        ArgumentNullException.ThrowIfNull(metric);

        return !double.IsFinite(value) ||
               !IsWithinExpectedRange(metric, value);
    }
}

//References
//BillWagner (2026). Nullable reference types - C#. [online] MicrosoftLearn. Available at: https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/null-safety/nullable-reference-types [Accessed 12 Sept. 2026].
//dotnet-bot (2026). IReadOnlyList Interface (System.Collections.Generic). [online] MicrosoftLearn. Available at: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.ireadonlylist-1?view=net-10.0 [Accessed 12 Sept. 2026].