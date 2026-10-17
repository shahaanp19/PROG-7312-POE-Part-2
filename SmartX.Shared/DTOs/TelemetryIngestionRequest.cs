
using System.ComponentModel.DataAnnotations;

namespace SmartX.Shared.DTOs;

public class TelemetryIngestionRequest
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
}
