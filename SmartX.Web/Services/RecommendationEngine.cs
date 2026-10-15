using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartX.Web.Services;

public sealed class RecommendationEngine
{
    private static readonly TimeSpan StaleTelemetryThreshold =
        TimeSpan.FromMinutes(15);

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
        var now = DateTimeOffset.UtcNow;
        var recommendations = new List<SmartXRecommendation>();

        var readings = telemetry?
            .Where(reading => reading is not null)
            .ToList() ?? new List<TelemetryObservation>();

        var queuedCommands = _commandState.Queue;
        var history = _commandState.History;
        var activeErrors = _commandState.ActiveErrors;

        AnalyzeTelemetry(readings, recommendations, now);
        AnalyzeCommandQueue(queuedCommands, recommendations);
        AnalyzeCommandHistory(history, recommendations);
        AnalyzeActiveErrors(activeErrors, recommendations);
        AnalyzeSearchQuery(
            searchQuery,
            queuedCommands.Count,
            history.Count,
            recommendations);

        return recommendations
            .GroupBy(
                recommendation => string.Join(
                    "|",
                    recommendation.Title,
                    recommendation.DeviceId),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.Severity)
                .First())
            .OrderBy(item => item.Severity)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AnalyzeTelemetry(
        IReadOnlyCollection<TelemetryObservation> readings,
        ICollection<SmartXRecommendation> recommendations,
        DateTimeOffset now)
    {
        foreach (var reading in readings)
        {
            if (string.IsNullOrWhiteSpace(reading.DeviceId) ||
                string.IsNullOrWhiteSpace(reading.Metric))
            {
                continue;
            }

            var deviceId = reading.DeviceId.Trim();
            var metric = reading.Metric.Trim();
            var normalizedMetric = metric.ToLowerInvariant();
            var value = reading.Value;
            var unit = reading.Unit?.Trim() ?? string.Empty;

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                recommendations.Add(new SmartXRecommendation(
                    "Invalid telemetry value",
                    $"{deviceId}: metric '{metric}' contains a non-finite value. Verify the sensor and ingestion pipeline.",
                    RecommendationSeverity.Warning,
                    deviceId));

                continue;
            }

            if (reading.ObservedAt > now.AddMinutes(5))
            {
                recommendations.Add(new SmartXRecommendation(
                    "Telemetry timestamp is in the future",
                    $"{deviceId}: metric '{metric}' has a timestamp of {reading.ObservedAt:u}. Check device clock synchronization.",
                    RecommendationSeverity.Warning,
                    deviceId));

                continue;
            }

            if (IsTemperatureMetric(normalizedMetric) && value >= 80)
            {
                recommendations.Add(new SmartXRecommendation(
                    "High temperature detected",
                    $"{deviceId}: {FormatValue(value, unit)}. Inspect device cooling, sensor accuracy, and operating conditions.",
                    value >= 95
                        ? RecommendationSeverity.Critical
                        : RecommendationSeverity.Warning,
                    deviceId));
            }

            if (IsBatteryMetric(normalizedMetric) && value <= 20)
            {
                recommendations.Add(new SmartXRecommendation(
                    "Low battery level",
                    $"{deviceId}: battery level is {FormatValue(value, unit)}. Schedule charging or battery replacement.",
                    value <= 10
                        ? RecommendationSeverity.Critical
                        : RecommendationSeverity.Warning,
                    deviceId));
            }

            if (IsSignalMetric(normalizedMetric) && value <= -85)
            {
                recommendations.Add(new SmartXRecommendation(
                    "Weak network signal",
                    $"{deviceId}: signal strength is {FormatValue(value, unit)}. Check device placement, interference, and gateway coverage.",
                    value <= -100
                        ? RecommendationSeverity.Critical
                        : RecommendationSeverity.Warning,
                    deviceId));
            }

            if (IsHumidityMetric(normalizedMetric) && value >= 90)
            {
                recommendations.Add(new SmartXRecommendation(
                    "High humidity detected",
                    $"{deviceId}: humidity is {FormatValue(value, unit)}. Verify the reading and inspect environmental conditions.",
                    value >= 98
                        ? RecommendationSeverity.Critical
                        : RecommendationSeverity.Warning,
                    deviceId));
            }

            var age = now - reading.ObservedAt;

            if (age > StaleTelemetryThreshold)
            {
                recommendations.Add(new SmartXRecommendation(
                    "Telemetry reading is stale",
                    $"{deviceId}: '{metric}' was last observed {FormatAge(age)} ago. Verify sensor reporting and network connectivity.",
                    RecommendationSeverity.Warning,
                    deviceId));
            }
        }

        AnalyzeTelemetryGaps(readings, recommendations, now);
    }

    private static void AnalyzeTelemetryGaps(
        IReadOnlyCollection<TelemetryObservation> readings,
        ICollection<SmartXRecommendation> recommendations,
        DateTimeOffset now)
    {
        var latestByDevice = readings
            .Where(reading =>
                !string.IsNullOrWhiteSpace(reading.DeviceId))
            .GroupBy(
                reading => reading.DeviceId.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                DeviceId = group.Key,
                Latest = group.Max(reading => reading.ObservedAt)
            });

        foreach (var device in latestByDevice)
        {
            if (now - device.Latest <= StaleTelemetryThreshold)
                continue;

            recommendations.Add(new SmartXRecommendation(
                "Device telemetry may be offline",
                $"{device.DeviceId}: the latest supplied telemetry is {FormatAge(now - device.Latest)} old. Check connectivity and sensor reporting.",
                RecommendationSeverity.Warning,
                device.DeviceId));
        }
    }

    private static void AnalyzeCommandQueue(
        IReadOnlyList<CommandRecord> queuedCommands,
        ICollection<SmartXRecommendation> recommendations)
    {
        var criticalCommands = queuedCommands
            .Where(command => command.Status == "Queued")
            .Where(command => string.Equals(
                command.Priority,
                "Critical",
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (criticalCommands.Count > 0)
        {
            recommendations.Add(new SmartXRecommendation(
                "Critical commands are pending",
                $"{criticalCommands.Count} critical command(s) are waiting. Review their targets and priority before routine operations.",
                RecommendationSeverity.Critical));
        }

        var highPriorityCount = queuedCommands.Count(command =>
            command.Status == "Queued" &&
            string.Equals(
                command.Priority,
                "High",
                StringComparison.OrdinalIgnoreCase));

        if (highPriorityCount > 0)
        {
            recommendations.Add(new SmartXRecommendation(
                "High-priority commands are pending",
                $"{highPriorityCount} high-priority command(s) remain queued. Review the queue for time-sensitive operations.",
                RecommendationSeverity.Warning));
        }

        var pendingCount = queuedCommands.Count(
            command => command.Status == "Queued");

        if (pendingCount >= 10)
        {
            recommendations.Add(new SmartXRecommendation(
                "Command queue backlog",
                $"{pendingCount} commands are waiting. Review command throughput and verify that queue processing is keeping pace.",
                pendingCount >= 25
                    ? RecommendationSeverity.Critical
                    : RecommendationSeverity.Warning));
        }

        var duplicatePendingGroups = queuedCommands
            .Where(command => command.Status == "Queued")
            .GroupBy(
                command => $"{command.DeviceId.Trim()}|{command.Command.Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 3)
            .OrderByDescending(group => group.Count())
            .Take(3);

        foreach (var group in duplicatePendingGroups)
        {
            var sample = group.First();

            recommendations.Add(new SmartXRecommendation(
                "Repeated commands in queue",
                $"{group.Count()} copies of '{sample.Command}' are pending for {sample.DeviceId}. Check for accidental duplicate submissions.",
                RecommendationSeverity.Warning,
                sample.DeviceId));
        }
    }

    private static void AnalyzeCommandHistory(
        IReadOnlyList<CommandRecord> history,
        ICollection<SmartXRecommendation> recommendations)
    {
        var recentCommands = history
            .OrderByDescending(command => command.CreatedAt)
            .ToList();

        var repeatedCommands = recentCommands
            .Where(command =>
                command.Status is "Queued" or "Processed locally")
            .GroupBy(
                command => $"{command.DeviceId.Trim()}|{command.Command.Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 3)
            .OrderByDescending(group => group.Count())
            .Take(3);

        foreach (var group in repeatedCommands)
        {
            var sample = group.First();

            recommendations.Add(new SmartXRecommendation(
                "Repeated command activity",
                $"'{sample.Command}' appears {group.Count()} times in recorded activity for {sample.DeviceId}. Verify that repeated submissions are intentional.",
                RecommendationSeverity.Info,
                sample.DeviceId));
        }

        var oldestPending = recentCommands
            .Where(command => command.Status == "Queued")
            .OrderBy(command => command.CreatedAt)
            .FirstOrDefault();

        if (oldestPending is not null)
        {
            var age = DateTimeOffset.UtcNow - oldestPending.CreatedAt;

            if (age > TimeSpan.FromMinutes(10))
            {
                recommendations.Add(new SmartXRecommendation(
                    "Command waiting for processing",
                    $"Command '{oldestPending.Command}' for {oldestPending.DeviceId} has been queued for {FormatAge(age)}.",
                    RecommendationSeverity.Warning,
                    oldestPending.DeviceId));
            }
        }
    }

    private static void AnalyzeActiveErrors(
        IReadOnlyCollection<string> activeErrors,
        ICollection<SmartXRecommendation> recommendations)
    {
        if (activeErrors.Count == 0)
            return;

        recommendations.Add(new SmartXRecommendation(
            "Active errors require attention",
            $"{activeErrors.Count} active error(s) remain unresolved. Review the error registry and resolve issues after verification.",
            activeErrors.Count >= 5
                ? RecommendationSeverity.Critical
                : RecommendationSeverity.Warning));

        foreach (var error in activeErrors.Take(3))
        {
            recommendations.Add(new SmartXRecommendation(
                "Unresolved error",
                $"Active error: {error}. Investigate the underlying condition and resolve it when confirmed.",
                RecommendationSeverity.Warning,
                error));
        }
    }

    private static void AnalyzeSearchQuery(
        string? searchQuery,
        int queuedCount,
        int historyCount,
        ICollection<SmartXRecommendation> recommendations)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
            return;

        var query = searchQuery.Trim();

        if (ContainsAny(query, "offline", "disconnect", "unreachable"))
        {
            recommendations.Add(new SmartXRecommendation(
                "Investigate device connectivity",
                "Check recent telemetry timestamps, gateway reachability, signal quality, and device connectivity before retrying commands.",
                RecommendationSeverity.Warning));
        }

        if (ContainsAny(query, "temperature", "overheat", "thermal"))
        {
            recommendations.Add(new SmartXRecommendation(
                "Review thermal telemetry",
                "Compare recent temperature readings against operating limits and inspect sustained increases rather than relying on a single reading.",
                RecommendationSeverity.Info));
        }

        if (ContainsAny(query, "battery", "power", "low energy"))
        {
            recommendations.Add(new SmartXRecommendation(
                "Review device power status",
                "Inspect battery telemetry, power-related alerts, and recent reporting gaps before scheduling maintenance.",
                RecommendationSeverity.Info));
        }

        if (ContainsAny(query, "command", "queue", "history", "failed"))
        {
            recommendations.Add(new SmartXRecommendation(
                "Review command activity",
                $"The application records {queuedCount} queued command(s) and {historyCount} command record(s). Review their statuses and device targets.",
                RecommendationSeverity.Info));
        }
    }

    private static bool IsTemperatureMetric(string metric) =>
        metric.Contains("temperature", StringComparison.OrdinalIgnoreCase)
        || metric is "temp" or "temp_c" or "temp_f";

    private static bool IsBatteryMetric(string metric) =>
        metric.Contains("battery", StringComparison.OrdinalIgnoreCase)
        || metric.Contains("charge_level", StringComparison.OrdinalIgnoreCase);

    private static bool IsSignalMetric(string metric) =>
        metric.Contains("signal", StringComparison.OrdinalIgnoreCase)
        || metric.Contains("rssi", StringComparison.OrdinalIgnoreCase);

    private static bool IsHumidityMetric(string metric) =>
        metric.Contains("humidity", StringComparison.OrdinalIgnoreCase)
        || metric is "rh" or "relative_humidity";

    private static bool ContainsAny(
        string text,
        params string[] terms) =>
        terms.Any(term => text.Contains(
            term,
            StringComparison.OrdinalIgnoreCase));

    private static string FormatValue(double value, string unit) =>
        string.IsNullOrWhiteSpace(unit)
            ? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : $"{value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {unit}";

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            return "less than a minute";

        if (age.TotalDays >= 1)
            return $"{(int)age.TotalDays}d {age.Hours}h";

        if (age.TotalHours >= 1)
            return $"{(int)age.TotalHours}h {age.Minutes}m";

        if (age.TotalMinutes >= 1)
            return $"{(int)age.TotalMinutes}m";

        return "less than a minute";
    }
}

public sealed class TelemetryObservation
{
    public string DeviceId { get; init; } = string.Empty;

    public string Metric { get; init; } = string.Empty;

    public double Value { get; init; }

    public string Unit { get; init; } = string.Empty;

    public DateTimeOffset ObservedAt { get; init; } =
        DateTimeOffset.UtcNow;
}

public sealed class SmartXRecommendation
{
    public SmartXRecommendation(
        string title,
        string description,
        RecommendationSeverity severity,
        string? deviceId = null)
    {
        Title = title;
        Description = description;
        Severity = severity;
        DeviceId = deviceId ?? string.Empty;
    }

    public string Title { get; }

    public string Description { get; }

    public RecommendationSeverity Severity { get; }

    public string DeviceId { get; }
}

public enum RecommendationSeverity
{
    Critical = 0,
    Warning = 1,
    Info = 2
}