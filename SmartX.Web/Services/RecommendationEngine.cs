
using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartX.Web.Services;

public sealed class RecommendationEngine
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ForecastWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TrendHistoryWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinimumTrendSpan = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FutureTimestampTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CommandFailureWindow = TimeSpan.FromHours(24);

    private const int TrendReadingLimit = 6;
    private const int MinimumTrendReadings = 4;

    private readonly CommandStateService _commandState;

    public RecommendationEngine(CommandStateService commandState)
    {
        _commandState = commandState
            ?? throw new ArgumentNullException(nameof(commandState));
    }

    public IReadOnlyList<SmartXRecommendation> Generate(
        IEnumerable<TelemetryObservation>? telemetry = null,
        string? searchQuery = null)
    {
        var recommendations = new List<SmartXRecommendation>();
        var observations = (telemetry ?? Enumerable.Empty<TelemetryObservation>())
            .Where(x => x is not null)
            .ToList();

        var now = DateTimeOffset.UtcNow;

        AnalyzeTelemetry(observations, recommendations, now);
        AnalyzeTrends(observations, recommendations, now);
        AnalyzeCommandQueue(recommendations);
        AnalyzeCommandHistory(recommendations, now);
        AnalyzeActiveErrors(recommendations);
        AnalyzeSearchQuery(searchQuery, recommendations);

        return recommendations
            .GroupBy(
                x => $"{x.Title}|{x.DeviceId}|{x.Description}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(x => x.Severity).First())
            .OrderBy(x => x.Severity)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DeviceId)
            .ToList()
            .AsReadOnly();
    }

    private static void AnalyzeTelemetry(
        IReadOnlyCollection<TelemetryObservation> observations,
        ICollection<SmartXRecommendation> recommendations,
        DateTimeOffset now)
    {
        var latestReadings = observations
            .GroupBy(x => new
            {
                x.DeviceId,
                Metric = NormalizeMetric(x.Metric)
            })
            .Select(group => group
                .OrderByDescending(x => AsUtc(x.ObservedAt))
                .First());

        foreach (var item in latestReadings)
        {
            var metric = string.IsNullOrWhiteSpace(item.Metric)
                ? "Telemetry"
                : item.Metric.Trim();

            var normalized = NormalizeMetric(metric);
            var observedAt = AsUtc(item.ObservedAt);

            if (item.DeviceId == Guid.Empty)
            {
                Add(
                    recommendations,
                    "Invalid sensor identity",
                    $"{metric} has an empty sensor identifier. Verify sensor registration and telemetry mapping.",
                    RecommendationSeverity.Warning);
                continue;
            }

            if (!double.IsFinite(item.Value))
            {
                Add(
                    recommendations,
                    "Invalid telemetry reading",
                    $"{metric} returned a non-finite value. Inspect the sensor and ingestion pipeline.",
                    RecommendationSeverity.Critical,
                    item.DeviceId);
                continue;
            }

            if (observedAt > now.Add(FutureTimestampTolerance))
            {
                Add(
                    recommendations,
                    "Future telemetry timestamp",
                    $"{metric} has a timestamp more than five minutes ahead of the server clock. Check clock synchronisation and UTC conversion.",
                    RecommendationSeverity.Warning,
                    item.DeviceId);
                continue;
            }

            if (observedAt > now)
            {
                Add(
                    recommendations,
                    "Telemetry clock skew",
                    $"{metric} has a timestamp slightly ahead of the server clock. Verify clock synchronisation.",
                    RecommendationSeverity.Info,
                    item.DeviceId);
            }

            if (observedAt < now - StaleThreshold)
            {
                Add(
                    recommendations,
                    "Stale telemetry",
                    $"{metric} was last observed {FormatDuration(now - observedAt)} ago at {observedAt:u}. Check device connectivity, sensor availability and ingestion.",
                    RecommendationSeverity.Warning,
                    item.DeviceId);
            }

            if (!IsRecognisedRiskMetric(normalized))
                continue;

            var direction = GetThresholdDirection(normalized);

            var hasConfiguredThresholds =
                item.WarningThreshold != 0 ||
                item.CriticalThreshold != 0;

            if (hasConfiguredThresholds &&
                !HasConfiguredThresholds(
                    item.WarningThreshold,
                    item.CriticalThreshold,
                    direction))
            {
                Add(
                    recommendations,
                    "Invalid telemetry thresholds",
                    $"{metric} has missing, non-finite or incorrectly ordered warning and critical thresholds. Correct the sensor configuration.",
                    RecommendationSeverity.Warning,
                    item.DeviceId);
                continue;
            }

            if (!TryGetRiskThresholds(
                    item,
                    normalized,
                    out var warningThreshold,
                    out var criticalThreshold,
                    out direction))
            {
                continue;
            }

            if (IsThresholdBreached(item.Value, criticalThreshold, direction))
            {
                Add(
                    recommendations,
                    $"Critical {metric}",
                    $"{metric} is {item.Value:0.##}{item.Unit}, at or beyond its critical threshold of {criticalThreshold:0.##}{item.Unit}. Investigate immediately and verify the underlying condition.",
                    RecommendationSeverity.Critical,
                    item.DeviceId);
            }
            else if (IsThresholdBreached(item.Value, warningThreshold, direction))
            {
                Add(
                    recommendations,
                    $"Elevated {metric}",
                    $"{metric} is {item.Value:0.##}{item.Unit}, at or beyond its warning threshold of {warningThreshold:0.##}{item.Unit}. Inspect the device and monitor subsequent readings.",
                    RecommendationSeverity.Warning,
                    item.DeviceId);
            }
        }
    }

    private static void AnalyzeTrends(
        IReadOnlyCollection<TelemetryObservation> observations,
        ICollection<SmartXRecommendation> recommendations,
        DateTimeOffset now)
    {
        var groups = observations
            .Where(x => x.DeviceId != Guid.Empty && double.IsFinite(x.Value))
            .GroupBy(x => new
            {
                x.DeviceId,
                Metric = NormalizeMetric(x.Metric)
            });

        foreach (var group in groups)
        {
            var readings = group
                .Where(x =>
                {
                    var timestamp = AsUtc(x.ObservedAt);
                    return timestamp <= now &&
                           timestamp >= now - TrendHistoryWindow;
                })
                .GroupBy(x => AsUtc(x.ObservedAt))
                .Select(timestampGroup => timestampGroup
                    .OrderByDescending(x => x.CriticalThreshold)
                    .First())
                .OrderBy(x => AsUtc(x.ObservedAt))
                .TakeLast(TrendReadingLimit)
                .ToList();

            if (readings.Count < MinimumTrendReadings)
                continue;

            var first = readings[0];
            var last = readings[^1];
            var elapsed = AsUtc(last.ObservedAt) - AsUtc(first.ObservedAt);

            if (elapsed < MinimumTrendSpan ||
                now - AsUtc(last.ObservedAt) > StaleThreshold)
            {
                continue;
            }

            if (!string.Equals(
                    first.Unit?.Trim(),
                    last.Unit?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    recommendations,
                    "Inconsistent telemetry units",
                    $"{group.Key.Metric} readings use different units within the trend window. Standardise units before forecasting.",
                    RecommendationSeverity.Warning,
                    group.Key.DeviceId);
                continue;
            }

            var metric = string.IsNullOrWhiteSpace(last.Metric)
                ? group.Key.Metric
                : last.Metric.Trim();

            var normalized = NormalizeMetric(metric);

            var risingMetric =
                normalized.Contains("temperature") ||
                normalized.Contains("humidity") ||
                normalized.Contains("pressure") ||
                normalized.Contains("power") ||
                normalized.Contains("voltage");

            var fallingMetric =
                normalized.Contains("battery") ||
                normalized.Contains("signal");

            if (!risingMetric && !fallingMetric)
                continue;

            if (!TryCalculateTrend(readings, out var ratePerMinute, out var fittedChange))
                continue;

            var firstValue = readings[0].Value;
            var lastValue = readings[^1].Value;

            var relativeChange = Math.Abs(fittedChange) /
                Math.Max(Math.Abs(firstValue), 1d);

            var concerning =
                (risingMetric && ratePerMinute > 0) ||
                (fallingMetric && ratePerMinute < 0);

            if (!concerning || relativeChange < 0.10)
                continue;

            Add(
                recommendations,
                $"{metric} trend requires attention",
                $"{metric} changed from {firstValue:0.##}{last.Unit} to {lastValue:0.##}{last.Unit} over {elapsed.TotalMinutes:0.#} minutes. Linear regression estimates a change rate of {ratePerMinute:+0.###;-0.###;0} units per minute.",
                relativeChange >= 0.50
                    ? RecommendationSeverity.Warning
                    : RecommendationSeverity.Info,
                group.Key.DeviceId);

            if (!TryGetRiskThresholds(
                    last,
                    normalized,
                    out _,
                    out var criticalThreshold,
                    out var direction))
            {
                continue;
            }

            var movingTowardThreshold = direction switch
            {
                ThresholdDirection.Above => ratePerMinute > 0,
                ThresholdDirection.Below => ratePerMinute < 0,
                _ => false
            };

            if (!movingTowardThreshold)
                continue;

            var distanceToThreshold = direction switch
            {
                ThresholdDirection.Above => criticalThreshold - lastValue,
                ThresholdDirection.Below => lastValue - criticalThreshold,
                _ => double.NaN
            };

            var speed = Math.Abs(ratePerMinute);

            if (!double.IsFinite(distanceToThreshold) ||
                distanceToThreshold <= 0 ||
                !double.IsFinite(speed) ||
                speed <= 0)
            {
                continue;
            }

            var minutesToThreshold = distanceToThreshold / speed;

            if (!double.IsFinite(minutesToThreshold) ||
                minutesToThreshold <= 0 ||
                minutesToThreshold > ForecastWindow.TotalMinutes)
            {
                continue;
            }

            var severity = minutesToThreshold <= 5
                ? RecommendationSeverity.Critical
                : RecommendationSeverity.Warning;

            Add(
                recommendations,
                $"{metric} critical threshold forecast",
                $"The recent linear trend estimates that {metric} may reach its critical threshold of {criticalThreshold:0.##}{last.Unit} in approximately {minutesToThreshold:0.#} minutes. This is an estimate, not a guarantee. Verify the readings and prepare corrective action.",
                severity,
                group.Key.DeviceId);
        }
    }

    private static bool TryCalculateTrend(
        IReadOnlyList<TelemetryObservation> readings,
        out double ratePerMinute,
        out double fittedChange)
    {
        ratePerMinute = 0;
        fittedChange = 0;

        if (readings.Count < MinimumTrendReadings)
            return false;

        var origin = AsUtc(readings[0].ObservedAt);

        var points = readings
            .Select(reading => (
                Minutes: (AsUtc(reading.ObservedAt) - origin).TotalMinutes,
                Value: reading.Value))
            .ToList();

        var meanX = points.Average(point => point.Minutes);
        var meanY = points.Average(point => point.Value);

        var numerator = 0d;
        var denominator = 0d;

        foreach (var point in points)
        {
            var deltaX = point.Minutes - meanX;
            numerator += deltaX * (point.Value - meanY);
            denominator += deltaX * deltaX;
        }

        if (denominator <= 0 || !double.IsFinite(denominator))
            return false;

        ratePerMinute = numerator / denominator;

        var fittedFirst = meanY +
            ratePerMinute * (points[0].Minutes - meanX);

        var fittedLast = meanY +
            ratePerMinute * (points[^1].Minutes - meanX);

        fittedChange = fittedLast - fittedFirst;

        return double.IsFinite(ratePerMinute) &&
               double.IsFinite(fittedChange);
    }

    private static bool IsThresholdBreached(
        double value,
        double threshold,
        ThresholdDirection direction) =>
        direction == ThresholdDirection.Above
            ? value >= threshold
            : value <= threshold;

    private static bool IsRecognisedRiskMetric(string metric) =>
        metric.Contains("battery") ||
        metric.Contains("signal") ||
        metric.Contains("temperature") ||
        metric.Contains("humidity") ||
        metric.Contains("pressure") ||
        metric.Contains("power") ||
        metric.Contains("voltage");

    private static ThresholdDirection GetThresholdDirection(string metric) =>
        metric.Contains("battery") || metric.Contains("signal")
            ? ThresholdDirection.Below
            : ThresholdDirection.Above;

    private static bool TryGetRiskThresholds(
        TelemetryObservation observation,
        string normalizedMetric,
        out double warningThreshold,
        out double criticalThreshold,
        out ThresholdDirection direction)
    {
        warningThreshold = observation.WarningThreshold;
        criticalThreshold = observation.CriticalThreshold;
        direction = GetThresholdDirection(normalizedMetric);

        if (!IsRecognisedRiskMetric(normalizedMetric))
            return false;

        if (!double.IsFinite(warningThreshold) ||
            !double.IsFinite(criticalThreshold))
        {
            return false;
        }

        if (HasConfiguredThresholds(
                warningThreshold,
                criticalThreshold,
                direction))
        {
            return true;
        }

        if (warningThreshold != 0 || criticalThreshold != 0)
            return false;

        if (normalizedMetric.Contains("battery"))
        {
            warningThreshold = 20;
            criticalThreshold = 10;
            direction = ThresholdDirection.Below;
            return true;
        }

        if (normalizedMetric.Contains("signal"))
        {
            warningThreshold = -85;
            criticalThreshold = -100;
            direction = ThresholdDirection.Below;
            return true;
        }

        if (normalizedMetric.Contains("temperature"))
        {
            warningThreshold = 80;
            criticalThreshold = 95;
            direction = ThresholdDirection.Above;
            return true;
        }

        if (normalizedMetric.Contains("humidity"))
        {
            warningThreshold = 90;
            criticalThreshold = 98;
            direction = ThresholdDirection.Above;
            return true;
        }

        return false;
    }

    private static bool HasConfiguredThresholds(
        double warningThreshold,
        double criticalThreshold,
        ThresholdDirection direction)
    {
        if (!double.IsFinite(warningThreshold) ||
            !double.IsFinite(criticalThreshold) ||
            (warningThreshold == 0 && criticalThreshold == 0))
        {
            return false;
        }

        return direction == ThresholdDirection.Above
            ? warningThreshold < criticalThreshold
            : warningThreshold > criticalThreshold;
    }

    private void AnalyzeCommandQueue(
        ICollection<SmartXRecommendation> recommendations)
    {
        var snapshot = _commandState.GetOperationalSnapshot();

        if (snapshot.CriticalPendingCommands > 0)
        {
            Add(
                recommendations,
                "Critical commands awaiting processing",
                $"{snapshot.CriticalPendingCommands} critical command(s) remain queued. Review their urgency and processing path.",
                RecommendationSeverity.Critical);
        }

        if (snapshot.HighPendingCommands > 0)
        {
            Add(
                recommendations,
                "High-priority commands awaiting processing",
                $"{snapshot.HighPendingCommands} high-priority command(s) are waiting. Review queue order and processing delays.",
                RecommendationSeverity.Warning);
        }

        if (snapshot.PendingCommands >= 25)
        {
            Add(
                recommendations,
                "Critical command backlog",
                $"{snapshot.PendingCommands} commands are queued. Investigate processing delays, duplicate submissions and stalled work.",
                RecommendationSeverity.Critical);
        }
        else if (snapshot.PendingCommands >= 10)
        {
            Add(
                recommendations,
                "Growing command backlog",
                $"{snapshot.PendingCommands} commands are queued. Review processing throughput and recent command activity.",
                RecommendationSeverity.Warning);
        }

        if (snapshot.OldestPendingCommandAge > TimeSpan.FromMinutes(30))
        {
            Add(
                recommendations,
                "Severely delayed command",
                $"The oldest pending command has been waiting {snapshot.OldestPendingCommandAge.TotalMinutes:0} minutes. Investigate blocked processing.",
                RecommendationSeverity.Critical);
        }
        else if (snapshot.OldestPendingCommandAge > TimeSpan.FromMinutes(10))
        {
            Add(
                recommendations,
                "Command processing delay",
                $"The oldest pending command has been waiting {snapshot.OldestPendingCommandAge.TotalMinutes:0} minutes. Review queue processing.",
                RecommendationSeverity.Warning);
        }

        foreach (var group in _commandState.Queue
                     .GroupBy(x => new
                     {
                         x.DeviceId,
                         Command = x.Command?.Trim() ?? string.Empty
                     })
                     .Where(group => group.Count() >= 3))
        {
            Add(
                recommendations,
                "Repeated queued commands",
                $"{group.Count()} copies of '{group.Key.Command}' target the same device. Check for duplicate submissions.",
                RecommendationSeverity.Warning,
                group.Key.DeviceId);
        }

        foreach (var device in snapshot.PendingCommandsByDevice
                     .Where(item => item.Value >= 5))
        {
            Add(
                recommendations,
                "Device command congestion",
                $"{device.Value} commands are waiting for the same device. Check connectivity, responsiveness and request volume.",
                RecommendationSeverity.Warning,
                device.Key);
        }
    }

    private void AnalyzeCommandHistory(
        ICollection<SmartXRecommendation> recommendations,
        DateTimeOffset now)
    {
        var history = _commandState.History
            .Where(record =>
            {
                var createdAt = AsUtc(record.CreatedAt);
                return createdAt >= now - CommandFailureWindow &&
                       createdAt <= now;
            })
            .ToList();

        foreach (var group in history
                     .Where(x => x.Status.ToString().Contains(
                         "Fail",
                         StringComparison.OrdinalIgnoreCase))
                     .GroupBy(x => new
                     {
                         x.DeviceId,
                         Command = x.Command?.Trim() ?? string.Empty
                     })
                     .Where(group => group.Count() >= 3))
        {
            Add(
                recommendations,
                "Repeated command failures",
                $"Command '{group.Key.Command}' has failed {group.Count()} times in the last 24 hours. Investigate the device, command parameters and underlying error before retrying.",
                RecommendationSeverity.Critical,
                group.Key.DeviceId);
        }

        var oneHourAgo = now.AddHours(-1);

        var recentCount = _commandState.History.Count(record =>
        {
            var createdAt = AsUtc(record.CreatedAt);
            return createdAt >= oneHourAgo && createdAt <= now;
        });

        if (recentCount >= 50)
        {
            Add(
                recommendations,
                "Unusually high command activity",
                $"{recentCount} commands were recorded in the last hour. Check for excessive retries, duplicate requests or automation loops.",
                RecommendationSeverity.Warning);
        }
    }

    private void AnalyzeActiveErrors(
        ICollection<SmartXRecommendation> recommendations)
    {
        var errors = _commandState.ActiveErrors
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (errors.Count > 0)
        {
            Add(
                recommendations,
                "Unresolved operational errors",
                $"{errors.Count} active error(s) remain unresolved. Investigate root causes and verify recovery before resolving them.",
                errors.Count >= 10
                    ? RecommendationSeverity.Critical
                    : RecommendationSeverity.Warning);
        }

        foreach (var error in errors)
        {
            Add(
                recommendations,
                "Active error needs investigation",
                error,
                RecommendationSeverity.Warning);
        }
    }

    private static void AnalyzeSearchQuery(
        string? searchQuery,
        ICollection<SmartXRecommendation> recommendations)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
            return;

        var query = searchQuery.Trim().ToLowerInvariant();

        if (query.Contains("battery") || query.Contains("power"))
        {
            Add(
                recommendations,
                "Review power availability",
                "Compare recent battery and power readings, check connectivity and plan charging or replacement where required.",
                RecommendationSeverity.Info);
        }

        if (query.Contains("temperature") ||
            query.Contains("heat") ||
            query.Contains("cooling"))
        {
            Add(
                recommendations,
                "Review thermal conditions",
                "Compare recent temperature readings, inspect rising trends and verify cooling operation.",
                RecommendationSeverity.Info);
        }

        if (query.Contains("signal") ||
            query.Contains("offline") ||
            query.Contains("connectivity"))
        {
            Add(
                recommendations,
                "Review device connectivity",
                "Inspect signal strength, latest telemetry timestamps and ingestion failures.",
                RecommendationSeverity.Info);
        }

        if (query.Contains("error") ||
            query.Contains("failure") ||
            query.Contains("failed"))
        {
            Add(
                recommendations,
                "Review operational failures",
                "Inspect active errors and recent command failures before retrying affected operations.",
                RecommendationSeverity.Info);
        }
    }

    private static string NormalizeMetric(string? metric) =>
        (metric ?? string.Empty).Trim().ToLowerInvariant();

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{duration.TotalDays:0.#} days";

        if (duration.TotalHours >= 1)
            return $"{duration.TotalHours:0.#} hours";

        return $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))} minutes";
    }

    private static DateTimeOffset AsUtc(DateTimeOffset value) =>
        value.ToUniversalTime();

    private static DateTimeOffset AsUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc =>
                new DateTimeOffset(value, TimeSpan.Zero),

            DateTimeKind.Local =>
                new DateTimeOffset(value).ToUniversalTime(),

            _ =>
                new DateTimeOffset(
                    DateTime.SpecifyKind(value, DateTimeKind.Utc))
        };

    private static Guid? ToDeviceId(object? value)
    {
        if (value is Guid id)
            return id == Guid.Empty ? null : id;

        if (value is string text &&
            Guid.TryParse(text, out var parsed) &&
            parsed != Guid.Empty)
        {
            return parsed;
        }

        return null;
    }

    private static void Add(
        ICollection<SmartXRecommendation> recommendations,
        string title,
        string description,
        RecommendationSeverity severity,
        object? deviceId = null)
    {
        recommendations.Add(new SmartXRecommendation
        {
            Title = title,
            Description = description,
            Severity = severity,
            DeviceId = ToDeviceId(deviceId)
        });
    }

    private enum ThresholdDirection
    {
        Above,
        Below
    }
}

public sealed class TelemetryObservation
{
    public Guid DeviceId { get; init; }
    public string Metric { get; init; } = string.Empty;
    public double Value { get; init; }
    public string Unit { get; init; } = string.Empty;
    public DateTimeOffset ObservedAt { get; init; }
    public double WarningThreshold { get; init; }
    public double CriticalThreshold { get; init; }
}

public sealed class SmartXRecommendation
{
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public RecommendationSeverity Severity { get; init; }
    public Guid? DeviceId { get; init; }
}

public enum RecommendationSeverity
{
    Critical = 0,
    Warning = 1,
    Info = 2
}
