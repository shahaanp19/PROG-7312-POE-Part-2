
namespace SmartX.Web.Services;

public sealed class RecommendationEngine
{
    private static readonly TimeSpan StaleThreshold =
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
        var recommendations = new List<SmartXRecommendation>();
        var observations = (telemetry ?? Enumerable.Empty<TelemetryObservation>())
            .ToList();

        var now = DateTime.UtcNow;

        AnalyzeTelemetry(observations, recommendations, now);
        AnalyzeTrends(observations, recommendations);
        AnalyzeCommandQueue(recommendations, now);
        AnalyzeCommandHistory(recommendations, now);
        AnalyzeActiveErrors(recommendations);
        AnalyzeSearchQuery(searchQuery, recommendations);

        return recommendations
            .GroupBy(
                x => $"{x.Title}|{x.DeviceId}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(x => x.Severity).First())
            .OrderBy(x => x.Severity)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private static void AnalyzeTelemetry(
        IReadOnlyCollection<TelemetryObservation> observations,
        ICollection<SmartXRecommendation> recommendations,
        DateTime now)
    {
        foreach (var item in observations)
        {
            var metric = item.Metric?.Trim() ?? string.Empty;
            var normalized = metric.ToLowerInvariant();

            if (!double.IsFinite(item.Value))
            {
                Add(recommendations, "Invalid telemetry reading",
                    $"{metric} returned a non-finite value. Inspect the sensor.",
                    RecommendationSeverity.Critical, item.DeviceId);
                continue;
            }

            var observedAt = AsUtc(item.ObservedAt);

            if (observedAt > now.AddMinutes(5))
            {
                Add(recommendations, "Future telemetry timestamp",
                    $"{metric} has a future timestamp. Check device clock synchronisation.",
                    RecommendationSeverity.Warning, item.DeviceId);
            }

            if (now - observedAt > StaleThreshold)
            {
                Add(recommendations, "Stale telemetry",
                    $"{metric} was last observed at {observedAt:u}. Check connectivity and ingestion.",
                    RecommendationSeverity.Warning, item.DeviceId);
            }

            if (normalized.Contains("temperature"))
            {
                if (item.Value >= 95)
                    Add(recommendations, "Critical temperature",
                        $"Temperature is {item.Value:0.##}{item.Unit}. Investigate overheating immediately.",
                        RecommendationSeverity.Critical, item.DeviceId);
                else if (item.Value >= 80)
                    Add(recommendations, "Elevated temperature",
                        $"Temperature is {item.Value:0.##}{item.Unit}. Inspect cooling and equipment load.",
                        RecommendationSeverity.Warning, item.DeviceId);
            }

            if (normalized.Contains("battery"))
            {
                if (item.Value <= 10)
                    Add(recommendations, "Critical battery level",
                        $"Battery is at {item.Value:0.##}{item.Unit}. Charge or replace it promptly.",
                        RecommendationSeverity.Critical, item.DeviceId);
                else if (item.Value <= 20)
                    Add(recommendations, "Low battery level",
                        $"Battery is at {item.Value:0.##}{item.Unit}. Schedule charging or replacement.",
                        RecommendationSeverity.Warning, item.DeviceId);
            }

            if (normalized.Contains("signal"))
            {
                if (item.Value <= -100)
                    Add(recommendations, "Critical signal strength",
                        $"Signal is {item.Value:0.##}{item.Unit}. Check antenna and network coverage.",
                        RecommendationSeverity.Critical, item.DeviceId);
                else if (item.Value <= -85)
                    Add(recommendations, "Weak signal strength",
                        $"Signal is {item.Value:0.##}{item.Unit}. Check interference and connectivity.",
                        RecommendationSeverity.Warning, item.DeviceId);
            }

            if (normalized.Contains("humidity"))
            {
                if (item.Value >= 98)
                    Add(recommendations, "Critical humidity",
                        $"Humidity is {item.Value:0.##}{item.Unit}. Inspect moisture and condensation risks.",
                        RecommendationSeverity.Critical, item.DeviceId);
                else if (item.Value >= 90)
                    Add(recommendations, "High humidity",
                        $"Humidity is {item.Value:0.##}{item.Unit}. Check ventilation and moisture control.",
                        RecommendationSeverity.Warning, item.DeviceId);
            }
        }
    }

    private static void AnalyzeTrends(
        IReadOnlyCollection<TelemetryObservation> observations,
        ICollection<SmartXRecommendation> recommendations)
    {
        var groups = observations
            .Where(x => double.IsFinite(x.Value))
            .GroupBy(x => new
            {
                x.DeviceId,
                Metric = x.Metric ?? string.Empty
            });

        foreach (var group in groups)
        {
            var readings = group
                .OrderBy(x => AsUtc(x.ObservedAt))
                .TakeLast(6)
                .ToList();

            if (readings.Count < 4)
                continue;

            var first = readings[0];
            var last = readings[^1];

            var elapsedMinutes =
                (AsUtc(last.ObservedAt) - AsUtc(first.ObservedAt))
                .TotalMinutes;

            if (elapsedMinutes <= 0)
                continue;

            var change = last.Value - first.Value;

            var relativeChange = Math.Abs(change) /
                Math.Max(Math.Abs(first.Value), 1d);

            if (relativeChange < 0.20)
                continue;

            var metric = group.Key.Metric;
            var normalized = metric.ToLowerInvariant();

            var concerning =
                (change > 0 &&
                    (normalized.Contains("temperature") ||
                     normalized.Contains("humidity") ||
                     normalized.Contains("pressure") ||
                     normalized.Contains("power") ||
                     normalized.Contains("voltage"))) ||
                (change < 0 &&
                    (normalized.Contains("battery") ||
                     normalized.Contains("signal")));

            if (!concerning)
                continue;

            Add(recommendations, $"{metric} trend requires attention",
                $"{metric} changed from {first.Value:0.##}{first.Unit} to {last.Value:0.##}{last.Unit} over {elapsedMinutes:0.#} minutes. Review the trend before operating conditions deteriorate.",
                relativeChange >= 0.50
                    ? RecommendationSeverity.Warning
                    : RecommendationSeverity.Info,
                group.Key.DeviceId);
        }
    }

    private void AnalyzeCommandQueue(
        ICollection<SmartXRecommendation> recommendations,
        DateTime now)
    {
        var queue = _commandState.Queue.ToList();

        var criticalCount = queue.Count(x =>
            x.Priority.ToString().Contains(
                "Critical", StringComparison.OrdinalIgnoreCase));

        var highCount = queue.Count(x =>
            x.Priority.ToString().Contains(
                "High", StringComparison.OrdinalIgnoreCase));

        if (criticalCount > 0)
            Add(recommendations, "Critical commands awaiting processing",
                $"{criticalCount} critical command(s) are queued. Process and verify them promptly.",
                RecommendationSeverity.Critical);

        if (highCount > 0)
            Add(recommendations, "High-priority commands awaiting processing",
                $"{highCount} high-priority command(s) are waiting. Review the queue.",
                RecommendationSeverity.Warning);

        if (queue.Count >= 25)
            Add(recommendations, "Critical command backlog",
                $"{queue.Count} commands are queued. Investigate processing delays.",
                RecommendationSeverity.Critical);
        else if (queue.Count >= 10)
            Add(recommendations, "Growing command backlog",
                $"{queue.Count} commands are queued. Review processing speed and failures.",
                RecommendationSeverity.Warning);

        foreach (var group in queue
            .GroupBy(x => new
            {
                x.DeviceId,
                Command = x.Command ?? string.Empty
            })
            .Where(x => x.Count() >= 3))
        {
            Add(recommendations, "Repeated queued commands",
                $"{group.Count()} copies of '{group.Key.Command}' target the same device. Check for duplicate submissions.",
                RecommendationSeverity.Warning, group.Key.DeviceId);
        }

        foreach (var command in queue)
        {
            var age = now - AsUtc(command.CreatedAt);

            if (age > TimeSpan.FromMinutes(10))
            {
                Add(recommendations, "Command waiting too long",
                    $"Command '{command.Command}' has been waiting for {age.TotalMinutes:0} minutes.",
                    age > TimeSpan.FromMinutes(30)
                        ? RecommendationSeverity.Critical
                        : RecommendationSeverity.Warning,
                    command.DeviceId);
            }
        }
    }

    private void AnalyzeCommandHistory(
        ICollection<SmartXRecommendation> recommendations,
        DateTime now)
    {
        var history = _commandState.History.ToList();

        foreach (var group in history
            .Where(x => x.Status.ToString().Contains(
                "Fail", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => new
            {
                x.DeviceId,
                Command = x.Command ?? string.Empty
            })
            .Where(x => x.Count() >= 3))
        {
            Add(recommendations, "Repeated command failures",
                $"Command '{group.Key.Command}' has failed repeatedly. Investigate the device and command parameters.",
                RecommendationSeverity.Critical, group.Key.DeviceId);
        }

        var recentCount = history.Count(x =>
            AsUtc(x.CreatedAt) >= now.AddHours(-1));

        if (recentCount >= 50)
        {
            Add(recommendations, "Unusually high command activity",
                $"{recentCount} commands were recorded in the last hour. Check for excessive retries or automation loops.",
                RecommendationSeverity.Warning);
        }
    }

    private void AnalyzeActiveErrors(
        ICollection<SmartXRecommendation> recommendations)
    {
        var errors = _commandState.ActiveErrors.ToList();

        if (errors.Count > 0)
        {
            Add(recommendations, "Unresolved operational errors",
                $"{errors.Count} active error(s) remain unresolved. Review root causes before resolving them.",
                errors.Count >= 10
                    ? RecommendationSeverity.Critical
                    : RecommendationSeverity.Warning);
        }

        foreach (var error in errors)
        {
            Add(recommendations, "Active error needs investigation",
                error, RecommendationSeverity.Warning);
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
            Add(recommendations, "Review power availability",
                "Check battery levels, power readings and device connectivity.",
                RecommendationSeverity.Info);

        if (query.Contains("temperature") || query.Contains("heat") ||
            query.Contains("cooling"))
            Add(recommendations, "Review thermal conditions",
                "Compare recent temperature readings and verify cooling operation.",
                RecommendationSeverity.Info);

        if (query.Contains("signal") || query.Contains("offline") ||
            query.Contains("connectivity"))
            Add(recommendations, "Review device connectivity",
                "Inspect signal strength, telemetry timestamps and ingestion failures.",
                RecommendationSeverity.Info);

        if (query.Contains("error") || query.Contains("failure") ||
            query.Contains("failed"))
            Add(recommendations, "Review operational failures",
                "Inspect active errors and repeated command failures before retrying.",
                RecommendationSeverity.Info);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static DateTime AsUtc(DateTimeOffset value) =>
        value.UtcDateTime;

    private static Guid? ToDeviceId(object? value)
    {
        if (value is Guid id)
            return id;

        if (value is string text && Guid.TryParse(text, out var parsed))
            return parsed;

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
}

public sealed class TelemetryObservation
{
    public Guid DeviceId { get; init; }

    public string Metric { get; init; } = string.Empty;

    public double Value { get; init; }

    public string Unit { get; init; } = string.Empty;

    public DateTime ObservedAt { get; init; }
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
