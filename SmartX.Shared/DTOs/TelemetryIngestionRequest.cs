
using System;
using System.ComponentModel.DataAnnotations;

namespace SmartX.Shared.DTOs;

public class TelemetryIngestionRequest : IValidatableObject
{
    [Required]
    public Guid SensorDeviceId { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string MetricName { get; set; } = string.Empty;

    [Required]
    [StringLength(50, MinimumLength = 1)]
    public string Unit { get; set; } = string.Empty;

    public double Value { get; set; }

    public double MinimumExpectedValue { get; set; }

    public double MaximumExpectedValue { get; set; }

    public double WarningThreshold { get; set; }

    public double CriticalThreshold { get; set; }

    public bool IsEnabled { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(
        ValidationContext validationContext)
    {
        if (SensorDeviceId == Guid.Empty)
        {
            yield return new ValidationResult(
                "A valid sensor device ID is required.",
                new[] { nameof(SensorDeviceId) });
        }

        if (string.IsNullOrWhiteSpace(MetricName))
        {
            yield return new ValidationResult(
                "Metric name cannot be empty or whitespace.",
                new[] { nameof(MetricName) });
        }

        if (string.IsNullOrWhiteSpace(Unit))
        {
            yield return new ValidationResult(
                "Unit cannot be empty or whitespace.",
                new[] { nameof(Unit) });
        }

        if (!double.IsFinite(Value))
        {
            yield return new ValidationResult(
                "Telemetry value must be a finite number.",
                new[] { nameof(Value) });
        }

        if (!double.IsFinite(MinimumExpectedValue) ||
            !double.IsFinite(MaximumExpectedValue))
        {
            yield return new ValidationResult(
                "Expected minimum and maximum must be finite numbers.",
                new[]
                {
                    nameof(MinimumExpectedValue),
                    nameof(MaximumExpectedValue)
                });
        }
        else
        {
            if (MinimumExpectedValue > MaximumExpectedValue)
            {
                yield return new ValidationResult(
                    "Minimum expected value cannot exceed maximum expected value.",
                    new[]
                    {
                        nameof(MinimumExpectedValue),
                        nameof(MaximumExpectedValue)
                    });
            }

            if (double.IsFinite(Value) &&
                (Value < MinimumExpectedValue ||
                 Value > MaximumExpectedValue))
            {
                yield return new ValidationResult(
                    "Telemetry value is outside the configured expected range.",
                    new[] { nameof(Value) });
            }
        }

        if (!double.IsFinite(WarningThreshold) ||
            !double.IsFinite(CriticalThreshold))
        {
            yield return new ValidationResult(
                "Warning and critical thresholds must be finite numbers.",
                new[]
                {
                    nameof(WarningThreshold),
                    nameof(CriticalThreshold)
                });
        }
        else if (WarningThreshold > CriticalThreshold)
        {
            yield return new ValidationResult(
                "Warning threshold cannot exceed critical threshold.",
                new[]
                {
                    nameof(WarningThreshold),
                    nameof(CriticalThreshold)
                });
        }
    }
}
