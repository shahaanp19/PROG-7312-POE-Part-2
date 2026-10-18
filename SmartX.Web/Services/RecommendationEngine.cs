
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace SmartX.Web.Services;

public sealed class RecommendationEngine
{
    private readonly CommandStateService _commandState;

    private const int MaximumRecommendations = 20;
    private const int MinimumTrendReadings = 3;

    private static readonly TimeSpan StaleReadingAge =
        TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ForecastHorizon =
        TimeSpan.FromMinutes(30);

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

        var readings = (telemetry ?? Enumerable.Empty<TelemetryObservation>())
            .Where(x => x is not null)
            .Where(x => double.IsFinite(x.Value))
            .Where(x => x.ObservedAt != default)
            .OrderBy(x => x.ObservedAt)
            .ToList();

        AddTelemetryRecommendations(readings, recommendations);
        AddCommandRecommendations(recommendations);
        AddOperationalRecommendations(recommendations);

        var query = searchQuery?.Trim();

        if (!string.IsNullOrWhiteSpace(query))
        {
            recommendations = recommendations
                .Where(x =>
                    Contains(x.Title, query) ||
                    Contains(x.Description, query) ||
                    x.DeviceId?.ToString().Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
        }

        return recommendations
            .GroupBy(
                x => $"{x.Title}|{x.Description}|{x.DeviceId}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Severity)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecommendations)
            .ToList();
    }

    private static void AddTelemetryRecommendations(
        IReadOnlyList<TelemetryObservation> readings,
        ICollection<SmartXRecommendation> recommendations)
    {
        var now = DateTimeOffset.UtcNow;

        var groups = readings
            .GroupBy(x => new
            {
                x.DeviceId,
                Metric = (x.Metric ?? string.Empty).Trim()
            });

        foreach (var group in groups)
        {
            var history = group
                .OrderBy(x => x.ObservedAt)
                .ToList();

            if (history.Count == 0)
                continue;

            // Only the latest observation determines current status.
            // Older observations remain available for trend analysis.
            var latest = history[^1];
            var metric = DisplayMetric(latest.Metric);
            var value = FormatValue(latest.Value, latest.Unit);
            var age = now - latest.ObservedAt.ToUniversalTime();

            if (age > StaleReadingAge)
            {
                Add(
                    recommendations,
                    $"Stale {metric} telemetry",
                    $"The latest available {metric} reading is " +
                    $"{FormatAge(age)} old. Check device connectivity, " +
                    "sensor power, and telemetry ingestion.",
                    RecommendationSeverity.Warning,
                    latest.DeviceId);
            }

            if (IsPositiveFinite(latest.CriticalThreshold) &&
                latest.Value >= latest.CriticalThreshold)
            {
                Add(
                    recommendations,
                    $"Critical {metric} reading",
                    $"{metric} is {value}, at or above the critical threshold " +
                    $"of {FormatValue(latest.CriticalThreshold, latest.Unit)}. " +
                    "Inspect the device and take corrective action.",
                    RecommendationSeverity.Critical,
                    latest.DeviceId);
            }
            else if (IsPositiveFinite(latest.WarningThreshold) &&
                     latest.Value >= latest.WarningThreshold)
            {
                Add(
                    recommendations,
                    $"High {metric} reading",
                    $"{metric} is {value}, at or above the warning threshold " +
                    $"of {FormatValue(latest.WarningThreshold, latest.Unit)}. " +
                    "Monitor the device and investigate the cause.",
                    RecommendationSeverity.Warning,
                    latest.DeviceId);
            }

            AddTrendRecommendations(history, latest, recommendations);
        }
    }

    private static void AddTrendRecommendations(
        IReadOnlyList<TelemetryObservation> history,
        TelemetryObservation latest,
        ICollection<SmartXRecommendation> recommendations)
    {
        if (history.Count < MinimumTrendReadings)
            return;

        var first = history[0];

        if (latest.ObservedAt <= first.ObservedAt)
            return;

        var elapsedMinutes =
            (latest.ObservedAt - first.ObservedAt).TotalMinutes;

        if (elapsedMinutes <= 0)
            return;

        var change = latest.Value - first.Value;
        var scale = Math.Max(Math.Abs(first.Value), 1.0);
        var relativeChange = Math.Abs(change) / scale;
        var range = history.Max(x => x.Value) - history.Min(x => x.Value);

        // Ignore insignificant changes and nearly flat histories.
        if (range <= scale * 0.01 || relativeChange < 0.05)
            return;

        var slopePerMinute = CalculateRegressionSlope(history);

        if (!double.IsFinite(slopePerMinute))
            return;

        var forecast = latest.Value +
                       slopePerMinute * ForecastHorizon.TotalMinutes;

        if (!double.IsFinite(forecast))
            return;

        var isRising = slopePerMinute > 0;
        var metric = DisplayMetric(latest.Metric);
        var unit = latest.Unit;

        if (isRising &&
            IsPositiveFinite(latest.CriticalThreshold) &&
            latest.Value < latest.CriticalThreshold &&
            forecast >= latest.CriticalThreshold)
        {
            Add(
                recommendations,
                $"Predicted critical {metric}",
                $"{metric} is trending upward. A linear regression over " +
                $"recent readings projects approximately " +
                $"{FormatValue(forecast, unit)} in " +
                $"{ForecastHorizon.TotalMinutes:0} minutes, crossing the " +
                $"critical threshold of " +
                $"{FormatValue(latest.CriticalThreshold, unit)}. " +
                "Verify the trend with fresh readings and inspect the device.",
                RecommendationSeverity.Critical,
                latest.DeviceId);
        }
        else if (isRising &&
                 IsPositiveFinite(latest.WarningThreshold) &&
                 latest.Value < latest.WarningThreshold &&
                 forecast >= latest.WarningThreshold)
        {
            Add(
                recommendations,
                $"Predicted high {metric}",
                $"{metric} is trending upward and may cross the warning " +
                $"threshold of {FormatValue(latest.WarningThreshold, unit)} " +
                $"within {ForecastHorizon.TotalMinutes:0} minutes. " +
                "Monitor upcoming readings and investigate the cause.",
                RecommendationSeverity.Warning,
                latest.DeviceId);
        }
        else if (isRising && relativeChange >= 0.25)
        {
            Add(
                recommendations,
                $"Rapidly increasing {metric}",
                $"{metric} changed by {FormatValue(change, unit)} over " +
                $"{elapsedMinutes:0.#} minutes. Check whether the increase " +
                "is expected and continue monitoring.",
                RecommendationSeverity.Warning,
                latest.DeviceId);
        }
        else if (!isRising && relativeChange >= 0.25)
        {
            Add(
                recommendations,
                $"Rapidly decreasing {metric}",
                $"{metric} decreased by " +
                $"{FormatValue(Math.Abs(change), unit)} over " +
                $"{elapsedMinutes:0.#} minutes. Verify that the decline " +
                "is expected and that the sensor is reporting correctly.",
                RecommendationSeverity.Info,
                latest.DeviceId);
        }
    }

    // Least-squares regression using minutes relative to the first reading.
    // Relative time avoids unnecessarily large timestamp values.
    private static double CalculateRegressionSlope(
        IReadOnlyList<TelemetryObservation> history)
    {
        var origin = history[0].ObservedAt;
        var points = history
            .Select(x => new
            {
                X = (x.ObservedAt - origin).TotalMinutes,
                Y = x.Value
            })
            .ToList();

        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);

        var numerator = points.Sum(
            p => (p.X - meanX) * (p.Y - meanY));

        var denominator = points.Sum(
            p => (p.X - meanX) * (p.X - meanX));

        if (denominator <= 0 || !double.IsFinite(denominator))
            return double.NaN;

        return numerator / denominator;
    }

    private void AddCommandRecommendations(
        ICollection<SmartXRecommendation> recommendations)
    {
        var queue = GetMemberValue(_commandState, "Queue");

        foreach (var command in Enumerate(queue))
        {
            var status =
                GetMemberValue(command, "Status")?.ToString() ?? string.Empty;

            if (!IsFailedStatus(status))
                continue;

            Add(
                recommendations,
                "Command execution issue",
                $"A command appears to have failed or been rejected. " +
                $"Command: {ReadCommandName(command)}. Review its result " +
                "and device connectivity before retrying.",
                RecommendationSeverity.Warning,
                ReadGuid(command, "DeviceId"));
        }

        var history = GetMemberValue(_commandState, "History");

        foreach (var command in Enumerate(history))
        {
            var status =
                GetMemberValue(command, "Status")?.ToString() ?? string.Empty;

            if (!IsFailedStatus(status))
                continue;

            Add(
                recommendations,
                "Review failed command",
                $"Command history contains a failed or rejected command: " +
                $"{ReadCommandName(command)}. Review the command status and " +
                "device response before attempting it again.",
                RecommendationSeverity.Warning,
                ReadGuid(command, "DeviceId"));
        }
    }

    private void AddOperationalRecommendations(
        ICollection<SmartXRecommendation> recommendations)
    {
        var activeErrors = GetMemberValue(_commandState, "ActiveErrors");

        foreach (var error in Enumerate(activeErrors))
        {
            var message = error?.ToString()?.Trim();

            if (string.IsNullOrWhiteSpace(message))
                continue;

            Add(
                recommendations,
                "Active system error",
                $"An active operational error needs attention: {message}. " +
                "Investigate the cause and confirm that the error clears.",
                RecommendationSeverity.Warning,
                null);
        }

        // The existing service's snapshot method has an optional parameter.
        // Reflection must account for that parameter even when it is omitted
        // by callers.
        var snapshotMethod = _commandState.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
            {
                if (!string.Equals(
                    method.Name,
                    "GetOperationalSnapshot",
                    StringComparison.Ordinal))
                    return false;

                var parameters = method.GetParameters();

                return parameters.Length == 0 ||
                       (parameters.Length == 1 &&
                        parameters[0].IsOptional);
            });

        if (snapshotMethod is null)
            return;

        object? snapshot;

        try
        {
            var parameters = snapshotMethod.GetParameters();
            snapshot = parameters.Length == 0
                ? snapshotMethod.Invoke(_commandState, null)
                : snapshotMethod.Invoke(_commandState, new object?[] { null });
        }
        catch
        {
            return;
        }

        if (snapshot is null)
            return;

        var criticalPending =
            ReadNumber(snapshot, "CriticalPendingCommands");

        var highPending =
            ReadNumber(snapshot, "HighPendingCommands");

        var pending = ReadNumber(snapshot, "PendingCommands");

        if (criticalPending > 0)
        {
            Add(
                recommendations,
                "Critical commands awaiting execution",
                $"{criticalPending} critical command(s) are pending. " +
                "Inspect the command queue and resolve blocking device or " +
                "connectivity issues.",
                RecommendationSeverity.Critical,
                null);
        }

        if (highPending > 0)
        {
            Add(
                recommendations,
                "High-priority commands delayed",
                $"{highPending} high-priority command(s) are pending. " +
                "Review queue processing and device availability.",
                RecommendationSeverity.Warning,
                null);
        }
        else if (pending >= 10)
        {
            Add(
                recommendations,
                "Command queue backlog",
                $"{pending} commands are pending. Check command processing, " +
                "device connectivity, and whether retries are accumulating.",
                RecommendationSeverity.Warning,
                null);
        }
    }

    private static bool IsFailedStatus(string status) =>
        Contains(status, "fail") ||
        Contains(status, "error") ||
        Contains(status, "reject");

    private static bool IsPositiveFinite(double value) =>
        double.IsFinite(value) && value > 0;

    private static void Add(
        ICollection<SmartXRecommendation> recommendations,
        string title,
        string description,
        RecommendationSeverity severity,
        Guid? deviceId)
    {
        recommendations.Add(new SmartXRecommendation
        {
            Title = title,
            Description = description,
            Severity = severity,
            DeviceId = deviceId
        });
    }

    private static object? GetMemberValue(object? instance, string name)
    {
        if (instance is null)
            return null;

        var type = instance.GetType();

        try
        {
            var property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.IgnoreCase);

            if (property is not null &&
                property.GetIndexParameters().Length == 0)
                return property.GetValue(instance);

            var field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.IgnoreCase);

            return field?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<object?> Enumerate(object? value)
    {
        if (value is not IEnumerable enumerable || value is string)
            yield break;

        foreach (var item in enumerable)
            yield return item;
    }

    private static Guid? ReadGuid(object? instance, string propertyName)
    {
        var value = GetMemberValue(instance, propertyName);

        if (value is Guid guid)
            return guid;

        return Guid.TryParse(value?.ToString(), out var parsed)
            ? parsed
            : null;
    }

    private static int ReadNumber(object? instance, string propertyName)
    {
        var value = GetMemberValue(instance, propertyName);

        if (value is null)
            return 0;

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadCommandName(object? command)
    {
        var value = GetMemberValue(command, "Command")
                    ?? GetMemberValue(command, "Name")
                    ?? GetMemberValue(command, "CommandName");

        return string.IsNullOrWhiteSpace(value?.ToString())
            ? "Unknown command"
            : value!.ToString()!.Trim();
    }

    private static string DisplayMetric(string? metric) =>
        string.IsNullOrWhiteSpace(metric) ? "sensor metric" : metric.Trim();

    private static string FormatValue(double value, string? unit)
    {
        var formatted = value.ToString(
            "0.##",
            CultureInfo.InvariantCulture);

        return string.IsNullOrWhiteSpace(unit)
            ? formatted
            : $"{formatted} {unit.Trim()}";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
            return $"{age.TotalDays:0.#} days";

        if (age.TotalHours >= 1)
            return $"{age.TotalHours:0.#} hours";

        return $"{Math.Max(0, age.TotalMinutes):0} minutes";
    }

    private static bool Contains(string? source, string value) =>
        !string.IsNullOrWhiteSpace(source) &&
        source.Contains(value, StringComparison.OrdinalIgnoreCase);
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
