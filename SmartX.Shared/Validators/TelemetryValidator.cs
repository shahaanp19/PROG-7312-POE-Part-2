
using System;
using SmartX.Shared.Enums;
using SmartX.Shared.Models;

namespace SmartX.Shared.Validators;

public class TelemetryValidator
{
    private readonly TelemetryThresholdValidator _thresholdValidator;

    public TelemetryValidator(
        TelemetryThresholdValidator thresholdValidator)
    {
        _thresholdValidator = thresholdValidator
            ?? throw new ArgumentNullException(nameof(thresholdValidator));
    }

    public TelemetryValidationResult Validate(
        TelemetryMetric metric,
        double value)
    {
        ArgumentNullException.ThrowIfNull(metric);

        var result = new TelemetryValidationResult
        {
            IsValid = true,
            IsAnomaly = false,
            Severity = AlertSeverity.Info
        };

        if (!metric.IsEnabled)
        {
            result.IsValid = false;
            result.Errors.Add(
                "Telemetry metric validation is disabled.");
            result.Message = "Metric validation is disabled.";
            return result;
        }

        if (!double.IsFinite(value))
        {
            result.IsValid = false;
            result.IsAnomaly = true;
            result.Severity = AlertSeverity.Critical;
            result.Errors.Add(
                "Telemetry value must be a finite number.");
            result.Message = "Invalid numeric telemetry value.";
            return result;
        }

        if (!_thresholdValidator.HasValidThresholdConfiguration(metric))
        {
            result.IsValid = false;
            result.IsAnomaly = true;
            result.Severity = AlertSeverity.Critical;
            result.Errors.Add(
                "Telemetry threshold configuration is invalid.");
            result.Message = "Invalid telemetry threshold configuration.";
            return result;
        }

        var outsideExpectedRange =
            !_thresholdValidator.IsWithinExpectedRange(metric, value);

        var isCritical = _thresholdValidator.IsCritical(metric, value);
        var isWarning = _thresholdValidator.IsWarning(metric, value);

        if (outsideExpectedRange)
        {
            result.IsAnomaly = true;

            result.Warnings.Add(
                $"Telemetry value {value} is outside the expected " +
                $"range of {metric.MinimumExpectedValue} to " +
                $"{metric.MaximumExpectedValue}.");
        }

        if (isCritical)
        {
            result.IsAnomaly = true;
            result.Severity = AlertSeverity.Critical;
            result.Warnings.Add(
                $"Telemetry value {value} has reached or exceeded " +
                $"the critical threshold of {metric.CriticalThreshold}.");
            result.Message = "Critical telemetry threshold exceeded.";
        }
        else if (isWarning)
        {
            result.IsAnomaly = true;
            result.Severity = AlertSeverity.Warning;
            result.Warnings.Add(
                $"Telemetry value {value} has reached or exceeded " +
                $"the warning threshold of {metric.WarningThreshold}.");
            result.Message = "Telemetry warning threshold exceeded.";
        }
        else if (outsideExpectedRange)
        {
            // Preserve the existing behavior expected by the test suite.
            result.Severity = AlertSeverity.Info;
            result.Message = "Telemetry anomaly detected.";
        }

        return result;
    }
}


//References
//BillWagner (2026). Nullable reference types - C#. [online] MicrosoftLearn. Available at: https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/null-safety/nullable-reference-types [Accessed 12 Sept. 2026].
//dotnet-bot (2026). IReadOnlyList Interface (System.Collections.Generic). [online] MicrosoftLearn. Available at: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.ireadonlylist-1?view=net-10.0 [Accessed 12 Sept. 2026].