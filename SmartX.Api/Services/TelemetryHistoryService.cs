
using System.Collections.Concurrent;

namespace SmartX.Api.Services;

public sealed class TelemetryHistoryService
{
    private const int DefaultMaxReadingsPerSensor = 10_000;

    private readonly ConcurrentDictionary<Guid, SensorHistory> _history = new();
    private readonly int _maxReadingsPerSensor;

    public TelemetryHistoryService()
        : this(DefaultMaxReadingsPerSensor)
    {
    }

    public TelemetryHistoryService(int maxReadingsPerSensor)
    {
        if (maxReadingsPerSensor <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxReadingsPerSensor),
                "Maximum readings per sensor must be greater than zero.");
        }

        _maxReadingsPerSensor = maxReadingsPerSensor;
    }

    public void AddReading(Guid sensorId, double value) =>
        AddReading(sensorId, value, DateTimeOffset.UtcNow);

    public void AddReading(
        Guid sensorId,
        double value,
        DateTimeOffset observedAt) =>
        AddReading(
            sensorId, value, string.Empty, string.Empty,
            0, 0, observedAt);

    public void AddReading(
        Guid sensorId,
        double value,
        string metricName,
        string unit) =>
        AddReading(
            sensorId, value, metricName, unit,
            0, 0, DateTimeOffset.UtcNow);

    public void AddReading(
        Guid sensorId,
        double value,
        string metricName,
        string unit,
        DateTimeOffset observedAt) =>
        AddReading(
            sensorId, value, metricName, unit,
            0, 0, observedAt);

    public void AddReading(
        Guid sensorId,
        double value,
        string metricName,
        string unit,
        double warningThreshold,
        double criticalThreshold) =>
        AddReading(
            sensorId, value, metricName, unit,
            warningThreshold, criticalThreshold,
            DateTimeOffset.UtcNow);

    public void AddReading(
        Guid sensorId,
        double value,
        string metricName,
        string unit,
        double warningThreshold,
        double criticalThreshold,
        DateTimeOffset observedAt)
    {
        if (sensorId == Guid.Empty)
        {
            throw new ArgumentException(
                "Sensor ID cannot be empty.",
                nameof(sensorId));
        }

        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Telemetry values must be finite numbers.");
        }

        if (!double.IsFinite(warningThreshold) ||
            !double.IsFinite(criticalThreshold))
        {
            throw new ArgumentOutOfRangeException(
                nameof(warningThreshold),
                "Telemetry thresholds must be finite numbers.");
        }

        ArgumentNullException.ThrowIfNull(metricName);
        ArgumentNullException.ThrowIfNull(unit);

        var reading = new TelemetryHistoryReading(
            sensorId,
            value,
            observedAt.ToUniversalTime(),
            metricName.Trim(),
            unit.Trim(),
            warningThreshold,
            criticalThreshold);

        _history.GetOrAdd(
            sensorId,
            _ => new SensorHistory(_maxReadingsPerSensor))
            .Add(reading);
    }

    public IReadOnlyList<double> GetReadings(Guid sensorId) =>
        GetTimestampedReadings(sensorId)
            .Select(reading => reading.Value)
            .ToArray();

    public IReadOnlyList<TelemetryHistoryReading> GetTimestampedReadings(
        Guid sensorId) =>
        GetHistory(sensorId);

    public IReadOnlyList<TelemetryHistoryReading> GetTimestampedReadings(
        Guid sensorId,
        int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "Count must be greater than zero.");
        }

        var snapshot = GetHistory(sensorId);
        var start = Math.Max(0, snapshot.Count - count);

        return snapshot.Skip(start).ToArray();
    }

    /// <summary>
    /// Returns readings within an inclusive UTC time range,
    /// ordered from oldest to newest.
    /// </summary>
    public IReadOnlyList<TelemetryHistoryReading> GetReadingsBetween(
        Guid sensorId,
        DateTimeOffset from,
        DateTimeOffset to,
        int? limit = null)
    {
        if (from > to)
        {
            throw new ArgumentException(
                "The start time cannot be later than the end time.");
        }

        if (limit is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "Limit must be greater than zero.");
        }

        var history = GetHistory(sensorId);
        var start = LowerBound(history, from.ToUniversalTime());
        var results = new List<TelemetryHistoryReading>();

        for (var i = start; i < history.Count; i++)
        {
            var reading = history[i];

            if (reading.ObservedAt > to.ToUniversalTime())
                break;

            results.Add(reading);

            if (limit.HasValue && results.Count >= limit.Value)
                break;
        }

        return results;
    }

    /// <summary>
    /// Returns a snapshot containing the most recent readings
    /// across all sensors, globally ordered by observation time.
    /// </summary>
    public IReadOnlyList<TelemetryHistoryReading> GetAllTimestampedReadings(
        int countPerSensor = 100)
    {
        if (countPerSensor <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(countPerSensor),
                "Count per sensor must be greater than zero.");
        }

        return _history
            .SelectMany(entry =>
            {
                var snapshot = entry.Value.GetSnapshot();
                var start = Math.Max(0, snapshot.Count - countPerSensor);

                return snapshot.Skip(start);
            })
            .OrderBy(reading => reading.ObservedAt)
            .ThenBy(reading => reading.SensorId)
            .ToArray();
    }

    public IReadOnlyDictionary<Guid, int> GetHistorySummary() =>
        _history.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Count);

    public TelemetryTrendSummary? GetTrendSummary(
        Guid sensorId,
        int count = 20)
    {
        if (count < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "At least two readings are required for trend analysis.");
        }

        var readings = GetTimestampedReadings(sensorId, count);

        if (readings.Count < 2)
            return null;

        var first = readings[0];
        var last = readings[^1];
        var elapsedMinutes =
            (last.ObservedAt - first.ObservedAt).TotalMinutes;

        if (elapsedMinutes <= 0)
            return null;

        var values = readings.Select(x => x.Value).ToArray();
        var change = last.Value - first.Value;
        var slope = change / elapsedMinutes;

        if (!double.IsFinite(slope))
            return null;

        var mean = values.Average();
        var variance = values.Average(value =>
            (value - mean) * (value - mean));

        var standardDeviation = Math.Sqrt(variance);

        return new TelemetryTrendSummary(
            sensorId,
            readings.Count,
            first.ObservedAt,
            last.ObservedAt,
            first.Value,
            last.Value,
            change,
            slope,
            values.Min(),
            values.Max(),
            mean,
            standardDeviation,
            DetermineTrend(change, mean, standardDeviation));
    }

    public double[,] GetMatrix(Guid sensorId, int rows, int columns)
    {
        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rows),
                "Rows must be greater than zero.");
        }

        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(columns),
                "Columns must be greater than zero.");
        }

        var readings = GetReadings(sensorId);
        var matrix = new double[rows, columns];
        var capacity = (long)rows * columns;
        var startIndex = Math.Max(0L, readings.Count - capacity);

        for (long index = startIndex;
             index < readings.Count && index - startIndex < capacity;
             index++)
        {
            var matrixIndex = index - startIndex;

            matrix[
                (int)(matrixIndex / columns),
                (int)(matrixIndex % columns)] = readings[(int)index];
        }

        return matrix;
    }

    public int GetReadingCount(Guid sensorId) =>
        _history.TryGetValue(sensorId, out var history)
            ? history.Count
            : 0;

    public DateTimeOffset? GetLatestObservedAt(Guid sensorId)
    {
        if (!_history.TryGetValue(sensorId, out var history))
            return null;

        return history.GetLatestObservedAt();
    }

    public bool RemoveHistory(Guid sensorId) =>
        _history.TryRemove(sensorId, out _);

    private IReadOnlyList<TelemetryHistoryReading> GetHistory(Guid sensorId) =>
        _history.TryGetValue(sensorId, out var history)
            ? history.GetSnapshot()
            : Array.Empty<TelemetryHistoryReading>();

    private static int LowerBound(
        IReadOnlyList<TelemetryHistoryReading> readings,
        DateTimeOffset timestamp)
    {
        var low = 0;
        var high = readings.Count;

        while (low < high)
        {
            var middle = low + (high - low) / 2;

            if (readings[middle].ObservedAt < timestamp)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static string DetermineTrend(
        double change,
        double mean,
        double standardDeviation)
    {
        var scale = Math.Max(Math.Abs(mean), 1.0);
        var tolerance = Math.Max(
            scale * 0.001,
            standardDeviation * 0.05);

        if (change > tolerance)
            return "Increasing";

        if (change < -tolerance)
            return "Decreasing";

        return "Stable";
    }

    private sealed class SensorHistory
    {
        private readonly object _sync = new();
        private readonly List<TelemetryHistoryReading> _readings = new();
        private readonly int _capacity;

        public SensorHistory(int capacity)
        {
            _capacity = capacity;
        }

        public int Count
        {
            get
            {
                lock (_sync)
                    return _readings.Count;
            }
        }

        public void Add(TelemetryHistoryReading reading)
        {
            lock (_sync)
            {
                // Fast path for the normal case: readings arrive in order.
                if (_readings.Count == 0 ||
                    _readings[^1].ObservedAt <= reading.ObservedAt)
                {
                    _readings.Add(reading);
                }
                else
                {
                    // Binary search keeps out-of-order ingestion correctly
                    // ordered without sorting the entire history.
                    var index = FindInsertionIndex(reading.ObservedAt);
                    _readings.Insert(index, reading);
                }

                // Retain the newest readings if capacity is exceeded.
                if (_readings.Count > _capacity)
                {
                    var excess = _readings.Count - _capacity;
                    _readings.RemoveRange(0, excess);
                }
            }
        }

        public IReadOnlyList<TelemetryHistoryReading> GetSnapshot()
        {
            lock (_sync)
                return _readings.ToArray();
        }

        public DateTimeOffset? GetLatestObservedAt()
        {
            lock (_sync)
            {
                return _readings.Count == 0
                    ? null
                    : _readings[^1].ObservedAt;
            }
        }

        private int FindInsertionIndex(DateTimeOffset timestamp)
        {
            var low = 0;
            var high = _readings.Count;

            // Insert after existing readings with the same timestamp.
            while (low < high)
            {
                var middle = low + (high - low) / 2;

                if (_readings[middle].ObservedAt <= timestamp)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }
    }
}

public sealed record TelemetryHistoryReading(
    Guid SensorId,
    double Value,
    DateTimeOffset ObservedAt,
    string MetricName = "",
    string Unit = "",
    double WarningThreshold = 0,
    double CriticalThreshold = 0);

public sealed record TelemetryTrendSummary(
    Guid SensorId,
    int ReadingCount,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    double FirstValue,
    double LatestValue,
    double Change,
    double ChangePerMinute,
    double Minimum,
    double Maximum,
    double Average,
    double StandardDeviation,
    string Trend);
