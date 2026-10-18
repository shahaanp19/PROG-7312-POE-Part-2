
using System;
using System.Diagnostics;
using SmartX.Shared.DTOs;
using SmartX.Shared.Models;
using SmartX.Shared.Validators;

namespace SmartX.Api.Services;

public class TelemetryIngestionService
{
    private readonly TelemetryValidator _validator;

    public TelemetryIngestionService(
        TelemetryValidator validator)
    {
        _validator = validator
            ?? throw new ArgumentNullException(nameof(validator));
    }

    public TelemetryIngestionResult Process(
        TelemetryMetric metric,
        double value)
    {
        ArgumentNullException.ThrowIfNull(metric);

        var processedAtUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var validationResult = _validator.Validate(metric, value);

            return new TelemetryIngestionResult
            {
                Success = validationResult.IsValid,
                PacketId = Guid.NewGuid(),
                SensorDeviceId = metric.SensorDeviceId,
                Message = validationResult.Message ?? string.Empty,
                IsAnomaly = validationResult.IsAnomaly,
                ProcessingTimeMilliseconds =
                    stopwatch.Elapsed.TotalMilliseconds,
                ProcessedAtUtc = processedAtUtc
            };
        }
        finally
        {
            stopwatch.Stop();
        }
    }
}
