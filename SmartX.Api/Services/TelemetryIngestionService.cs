
using System.Diagnostics;
using SmartX.Shared.DTOs;
using SmartX.Shared.Models;
using SmartX.Shared.Validators;

namespace SmartX.Api.Services;

public class TelemetryIngestionService
{
    private readonly TelemetryValidator _validator;
    private readonly TelemetryHistoryService _historyService;

    public TelemetryIngestionService(
        TelemetryValidator validator,
        TelemetryHistoryService historyService)
    {
        _validator = validator
            ?? throw new ArgumentNullException(nameof(validator));

        _historyService = historyService
            ?? throw new ArgumentNullException(nameof(historyService));
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

            if (validationResult.IsValid &&
                metric.SensorDeviceId != Guid.Empty)
            {
                _historyService.AddReading(
                    metric.SensorDeviceId,
                    value,
                    processedAtUtc);
            }

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
