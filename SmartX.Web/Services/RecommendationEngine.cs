
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SmartX.Web.Services;

public sealed class RecommendationEngine
{
    private const int MaximumRecommendations = 20;
    private const int MinimumTrendReadings = 3;
    private const int MinimumForecastMinutes = 2;
    private const int MaximumSearchHistoryEntries = 50;
    private const int RepeatedSearchThreshold = 3;

    private static readonly TimeSpan StaleReadingAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ForecastHorizon = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RecentCommandWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DelayedCommandAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SearchAnalysisWindow = TimeSpan.FromMinutes(30);

    private static readonly HashSet<string> IgnoredSearchTerms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "from", "that", "this",
            "are", "was", "were", "has", "have", "show", "find",
            "search", "recommendation", "recommendations", "device"
        };

    private readonly CommandStateService _commandState;
    private readonly object _searchHistoryLock = new();
    private readonly Queue<SearchObservation> _searchHistory = new();

    public RecommendationEngine(CommandStateService commandState)
    {
        _commandState = commandState
            ?? throw new ArgumentNullException(nameof(commandState));
    }

    /// <summary>
    /// Records a search explicitly. Call this once when a user submits a search,
    /// not from a component's rendering method.
    /// </summary>
    public void RecordSearchQuery(string? query, int resultCount)
    {
        var normalizedQuery = NormalizeQuery(query);

        if (normalizedQuery.Length == 0)
            return;

        var observation = new SearchObservation(
            normalizedQuery,
            Math.Max(0, resultCount),
            DateTimeOffset.UtcNow);

        lock (_searchHistoryLock)
        {
            PruneSearchHistory(observation.Timestamp);

            _searchHistory.Enqueue(observation);

            while (_searchHistory.Count > MaximumSearchHistoryEntries)
                _searchHistory.Dequeue();
        }
    }

    public IReadOnlyList<SmartXRecommendation> Generate(
        IEnumerable<TelemetryObservation>? telemetry = null,
        string? searchQuery = null)
    {
        var recommendations = new List<SmartXRecommendation>();

        var readings = (telemetry ?? Enumerable.Empty<TelemetryObservation>())
            .Where(x => x is not null)
            .Where(x => x.DeviceId != Guid.Empty)
            .Where(x => double.IsFinite(x.Value))
            .Where(x => x.ObservedAt != default)
            .Where(x => !string.IsNullOrWhiteSpace(x.Metric))
            .OrderBy(x => x.ObservedAt)
            .ToList();

        AddTelemetryRecommendations(readings, recommendations);
        AddCommandRecommendations(recommendations);
        AddOperationalRecommendations(recommendations);
        AddCrossSignalRecommendations(readings, recommendations);
        AddSearchPatternRecommendations(recommendations);

        var query = NormalizeQuery(searchQuery);

        IEnumerable<SmartXRecommendation> result = recommendations;

        if (query.Length > 0)
        {
            result = result.Where(x =>
                Contains(x.Title, query) ||
                Contains(x.Description, query) ||
                x.DeviceId?.ToString().Contains(
                    query, StringComparison.OrdinalIgnoreCase) == true);
        }

        return result
            .GroupBy(
                x => $"{x.Title}|{x.Description}|{x.DeviceId}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Severity)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecommendations)
            .ToArray();
    }

    private static void AddTelemetryRecommendations(
        IReadOnlyList<TelemetryObservation> readings,
        ICollection<SmartXRecommendation> recommendations)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var group in readings.GroupBy(x => new
        {
            x.DeviceId,
            Metric = x.Metric.Trim()
        }))
        {
            var history = group.OrderBy(x => x.ObservedAt).ToArray();
            var latest = history[^1];

            var age = now - latest.ObservedAt.ToUniversalTime();
            var metric = DisplayMetric(latest.Metric);
            var value = FormatValue(latest.Value, latest.Unit);

            if (age > StaleReadingAge)
            {
                Add(
                    recommendations,
                    $"Stale {metric} telemetry",
                    $"The latest {metric} reading is {FormatAge(age)} old. " +
                    "Check device connectivity, sensor power, and telemetry ingestion.",
                    RecommendationSeverity.Warning,
                    latest.DeviceId);
            }

            if (IsValidThreshold(latest.CriticalThreshold) &&
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
            else if (IsValidThreshold(latest.WarningThreshold) &&
                     latest.Value >= latest.WarningThreshold)
            {
                Add(
                    recommendations,
                    $"High {metric} reading",
                    $"{metric} is {value}, at or above the warning threshold " +
                    $"of {FormatValue(latest.WarningThreshold, latest.Unit)}. " +
                    "Investigate the cause and monitor the next readings.",
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

        var cutoff = latest.ObservedAt - TimeSpan.FromHours(2);
        var recent = history.Where(x => x.ObservedAt >= cutoff).ToArray();

        if (recent.Length < MinimumTrendReadings)
            return;

        var elapsedMinutes =
            (recent[^1].ObservedAt - recent[0].ObservedAt).TotalMinutes;

        if (elapsedMinutes < MinimumForecastMinutes)
            return;

        var slope = CalculateRegressionSlope(recent);

        if (!double.IsFinite(slope))
            return;

        var origin = recent[0].ObservedAt;

        var residuals = recent.Select(x =>
            x.Value - (recent[0].Value +
                       slope * (x.ObservedAt - origin).TotalMinutes))
            .ToArray();

        var mean = residuals.Average();
        var noise = Math.Sqrt(
            residuals.Average(x => Math.Pow(x - mean, 2)));

        var change = latest.Value - recent[0].Value;
        var scale = Math.Max(Math.Abs(recent[0].Value), 1.0);
        var relativeChange = Math.Abs(change) / scale;

        if (relativeChange < 0.05)
            return;

        var trendMagnitude = Math.Abs(slope) * ForecastHorizon.TotalMinutes;

        if (noise > Math.Max(scale * 0.5, trendMagnitude * 2))
            return;

        var forecast = latest.Value + slope * ForecastHorizon.TotalMinutes;

        if (!double.IsFinite(forecast))
            return;

        var rising = slope > 0;
        var metric = DisplayMetric(latest.Metric);
        var unit = latest.Unit;

        if (rising &&
            IsValidThreshold(latest.CriticalThreshold) &&
            latest.Value < latest.CriticalThreshold &&
            forecast >= latest.CriticalThreshold)
        {
            Add(
                recommendations,
                $"Predicted critical {metric}",
                $"{metric} is rising. Recent linear regression projects " +
                $"{FormatValue(forecast, unit)} in 30 minutes, crossing the " +
                $"critical threshold of {FormatValue(latest.CriticalThreshold, unit)}. " +
                "Verify with fresh readings and inspect the device now.",
                RecommendationSeverity.Critical,
                latest.DeviceId);
        }
        else if (rising &&
                 IsValidThreshold(latest.WarningThreshold) &&
                 latest.Value < latest.WarningThreshold &&
                 forecast >= latest.WarningThreshold)
        {
            Add(
                recommendations,
                $"Predicted high {metric}",
                $"{metric} is rising and may cross the warning threshold " +
                $"{FormatValue(latest.WarningThreshold, unit)} within 30 minutes. " +
                "Check load, environmental conditions, and upcoming readings.",
                RecommendationSeverity.Warning,
                latest.DeviceId);
        }
        else if (relativeChange >= 0.25)
        {
            Add(
                recommendations,
                rising
                    ? $"Rapidly increasing {metric}"
                    : $"Rapidly decreasing {metric}",
                $"{metric} changed by {FormatValue(Math.Abs(change), unit)} " +
                $"over {elapsedMinutes:0.#} minutes. Confirm whether the change " +
                "is expected and inspect the sensor if it is not.",
                rising
                    ? RecommendationSeverity.Warning
                    : RecommendationSeverity.Info,
                latest.DeviceId);
        }
    }

    private static double CalculateRegressionSlope(
        IReadOnlyList<TelemetryObservation> history)
    {
        var origin = history[0].ObservedAt;

        var points = history.Select(x => new
        {
            X = (x.ObservedAt - origin).TotalMinutes,
            Y = x.Value
        }).ToArray();

        var meanX = points.Average(x => x.X);
        var meanY = points.Average(x => x.Y);
        var denominator = points.Sum(x => Math.Pow(x.X - meanX, 2));

        if (denominator <= 0 || !double.IsFinite(denominator))
            return double.NaN;

        return points.Sum(x => (x.X - meanX) * (x.Y - meanY)) /
               denominator;
    }

    private void AddCommandRecommendations(
        ICollection<SmartXRecommendation> recommendations)
    {
        var snapshot =
            _commandState.GetOperationalSnapshot(RecentCommandWindow);

        if (snapshot.OldestPendingCommandAge >= DelayedCommandAge)
        {
            Add(
                recommendations,
                "Commands delayed in queue",
                $"The oldest pending command is " +
                $"{FormatAge(snapshot.OldestPendingCommandAge)} old. " +
                "Check device availability, queue processing, and whether retries are accumulating.",
                snapshot.CriticalPendingCommands > 0
                    ? RecommendationSeverity.Critical
                    : RecommendationSeverity.Warning,
                null);
        }

        foreach (var command in _commandState.History)
        {
            if (!string.Equals(
                    command.Status,
                    "Failed",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            Add(
                recommendations,
                "Review failed command",
                $"Command '{command.Command}' for device '{command.DeviceId}' failed: " +
                $"{command.Details} Review the device response before retrying.",
                RecommendationSeverity.Warning,
                Guid.TryParse(command.DeviceId, out var id) ? id : null);
        }

        foreach (var command in _commandState.Queue)
        {
            if (!string.Equals(
                    command.Priority,
                    "Critical",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            Add(
                recommendations,
                "Critical command awaiting execution",
                $"Critical command '{command.Command}' for device '{command.DeviceId}' " +
                "is still pending. Verify connectivity and command processing.",
                RecommendationSeverity.Critical,
                Guid.TryParse(command.DeviceId, out var id) ? id : null);
        }
    }

    private void AddOperationalRecommendations(
        ICollection<SmartXRecommendation> recommendations)
    {
        var snapshot =
            _commandState.GetOperationalSnapshot(RecentCommandWindow);

        foreach (var error in snapshot.ActiveErrors.Take(5))
        {
            Add(
                recommendations,
                "Active system error",
                $"An active operational error requires attention: {error}. " +
                "Investigate its cause and resolve it when service is restored.",
                RecommendationSeverity.Warning,
                null);
        }

        if (snapshot.HighPendingCommands > 0)
        {
            Add(
                recommendations,
                "High-priority commands delayed",
                $"{snapshot.HighPendingCommands} high-priority command(s) are pending. " +
                "Review queue processing and device availability.",
                RecommendationSeverity.Warning,
                null);
        }

        if (snapshot.PendingCommands >= 10)
        {
            Add(
                recommendations,
                "Command queue backlog",
                $"{snapshot.PendingCommands} commands are pending. " +
                "Check processing throughput, device connectivity, and retry behaviour.",
                RecommendationSeverity.Warning,
                null);
        }

        if (snapshot.FailedCommands > 0)
        {
            Add(
                recommendations,
                "Command reliability needs review",
                $"{snapshot.FailedCommands} command(s) are recorded as failed. " +
                "Inspect recurring device failures and resolve underlying connectivity or command issues.",
                RecommendationSeverity.Warning,
                null);
        }
    }

    private void AddCrossSignalRecommendations(
        IReadOnlyList<TelemetryObservation> readings,
        ICollection<SmartXRecommendation> recommendations)
    {
        if (readings.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var snapshot =
            _commandState.GetOperationalSnapshot(RecentCommandWindow);

        var activeErrors = snapshot.ActiveErrors.ToArray();
        var recentFailedDeviceIds = _commandState.History
            .Where(command =>
                string.Equals(
                    command.Status,
                    "Failed",
                    StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.TryParse(
                    command.CreatedAt.ToString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var createdAt) &&
                now - createdAt.ToUniversalTime() >= TimeSpan.Zero &&
                now - createdAt.ToUniversalTime() <= RecentCommandWindow)
            .Select(command =>
                Guid.TryParse(command.DeviceId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        foreach (var group in readings.GroupBy(x => x.DeviceId))
        {
            var latest = group
                .OrderByDescending(x => x.ObservedAt)
                .First();

            var age = now - latest.ObservedAt.ToUniversalTime();

            if (age < TimeSpan.Zero || age > StaleReadingAge)
                continue;

            var deviceIdText = latest.DeviceId.ToString();

            var matchingError = activeErrors.Any(error =>
                !string.IsNullOrWhiteSpace(error) &&
                error.Contains(
                    deviceIdText,
                    StringComparison.OrdinalIgnoreCase));

            var hasRecentFailedCommand =
                recentFailedDeviceIds.Contains(latest.DeviceId);

            // Do not infer device-level correlation from an unrelated system error.
            if (!matchingError && !hasRecentFailedCommand)
                continue;

            var evidence = matchingError && hasRecentFailedCommand
                ? "An active error identifies this device, and a recent command for it failed."
                : matchingError
                    ? "An active error explicitly identifies this device."
                    : "A recent command for this device failed.";

            var title = matchingError && hasRecentFailedCommand
                ? "Multiple signals indicate a device issue"
                : "Device issue supported by operational evidence";

            Add(
                recommendations,
                title,
                $"{evidence} Fresh {DisplayMetric(latest.Metric)} telemetry is " +
                $"{FormatValue(latest.Value, latest.Unit)} for device {deviceIdText}. " +
                "Inspect this device's telemetry and command details before retrying commands.",
                matchingError && hasRecentFailedCommand
                    ? RecommendationSeverity.Critical
                    : RecommendationSeverity.Warning,
                latest.DeviceId);
        }
    }

    private void AddSearchPatternRecommendations(
        ICollection<SmartXRecommendation> recommendations)
    {
        SearchObservation[] recentSearches;
        var now = DateTimeOffset.UtcNow;

        lock (_searchHistoryLock)
        {
            PruneSearchHistory(now);
            recentSearches = _searchHistory.ToArray();
        }

        if (recentSearches.Length == 0)
            return;

        // Detect the same query being submitted repeatedly.
        foreach (var group in recentSearches.GroupBy(
                     x => x.Query,
                     StringComparer.OrdinalIgnoreCase))
        {
            var searches = group.OrderBy(x => x.Timestamp).ToArray();

            if (searches.Length < RepeatedSearchThreshold)
                continue;

            var zeroResultCount = searches.Count(x => x.ResultCount == 0);
            var query = Truncate(searches[0].Query, 80);

            if (zeroResultCount >= 2)
            {
                Add(
                    recommendations,
                    "Repeated searches are returning no results",
                    $"The query '{query}' was submitted {searches.Length} times " +
                    $"in the recent history, with {zeroResultCount} searches returning " +
                    "no recommendations. Check whether relevant telemetry, device identifiers, " +
                    "or recommendation rules are missing.",
                    RecommendationSeverity.Warning,
                    null);
            }
            else
            {
                Add(
                    recommendations,
                    "Repeated search pattern detected",
                    $"The query '{query}' was submitted {searches.Length} times recently. " +
                    "Review the matching recommendations and determine whether a recurring " +
                    "operational issue needs a lasting fix.",
                    RecommendationSeverity.Info,
                    null);
            }
        }

        // Detect terms recurring across different queries, not just duplicate searches.
        var recurringTerms = recentSearches
            .SelectMany(search => Tokenize(search.Query)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(term => term, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= RepeatedSearchThreshold)
            .OrderByDescending(group => group.Count())
            .Take(3);

        foreach (var termGroup in recurringTerms)
        {
            var term = termGroup.Key;
            var distinctQueries = recentSearches
                .Where(search => Tokenize(search.Query).Contains(
                    term,
                    StringComparer.OrdinalIgnoreCase))
                .Select(search => search.Query)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (distinctQueries < RepeatedSearchThreshold)
                continue;

            Add(
                recommendations,
                $"Recurring search topic: {term}",
                $"The term '{term}' appeared in {distinctQueries} distinct searches " +
                "during the recent history. Check whether the dashboard needs a more direct " +
                "recommendation, clearer device status, or an operational fix for this topic.",
                RecommendationSeverity.Info,
                null);
        }
    }

    private void PruneSearchHistory(DateTimeOffset now)
    {
        var cutoff = now - SearchAnalysisWindow;

        while (_searchHistory.Count > 0 &&
               _searchHistory.Peek().Timestamp < cutoff)
        {
            _searchHistory.Dequeue();
        }
    }

    private static IEnumerable<string> Tokenize(string query)
    {
        return query
            .Split(
                new[] { ' ', '\t', '\r', '\n', ',', '.', ':', ';', '/', '\\', '-', '_' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token =>
                token.Length >= 3 &&
                !IgnoredSearchTerms.Contains(token));
    }

    private static string NormalizeQuery(string? query)
    {
        return string.Join(
            " ",
            (query ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsValidThreshold(double value) =>
        double.IsFinite(value);

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

    private static string DisplayMetric(string? metric) =>
        string.IsNullOrWhiteSpace(metric) ? "sensor metric" : metric.Trim();

    private static string FormatValue(double value, string? unit)
    {
        var formatted = value.ToString("0.##", CultureInfo.InvariantCulture);
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

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength
            ? value
            : value[..(maximumLength - 3)] + "...";

    private static bool Contains(string? source, string value) =>
        !string.IsNullOrWhiteSpace(source) &&
        source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private sealed record SearchObservation(
        string Query,
        int ResultCount,
        DateTimeOffset Timestamp);
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
