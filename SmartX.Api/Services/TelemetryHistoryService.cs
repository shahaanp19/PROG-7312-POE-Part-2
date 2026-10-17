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

    public void AddReading(Guid sensorId, double value)
    {
        AddReading(sensorId, value, DateTimeOffset.UtcNow);
    }

    public void AddReading(
        Guid sensorId,
        double value,
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

        var reading = new TelemetryHistoryReading(
            sensorId,
            value,
            observedAt.ToUniversalTime());

        _history.GetOrAdd(
            sensorId,
            _ => new SensorHistory(_maxReadingsPerSensor))
            .Add(reading);
    }

    public IReadOnlyList<double> GetReadings(Guid sensorId)
    {
        return GetHistory(sensorId)
            .Select(reading => reading.Value)
            .ToArray();
    }

    public IReadOnlyList<TelemetryHistoryReading> GetTimestampedReadings(
        Guid sensorId)
    {
        return GetHistory(sensorId);
    }

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

        var readings = GetHistory(sensorId);

        return readings
            .TakeLast(count)
            .ToArray();
    }

    public double[,] GetMatrix(
        Guid sensorId,
        int rows,
        int columns)
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
            var row = (int)(matrixIndex / columns);
            var column = (int)(matrixIndex % columns);

            matrix[row, column] = readings[(int)index];
        }

        return matrix;
    }

    public int GetReadingCount(Guid sensorId)
    {
        return _history.TryGetValue(sensorId, out var history)
            ? history.Count
            : 0;
    }

    public DateTimeOffset? GetLatestObservedAt(Guid sensorId)
    {
        var readings = GetTimestampedReadings(sensorId);

        return readings.Count == 0
            ? null
            : readings[^1].ObservedAt;
    }

    public bool RemoveHistory(Guid sensorId)
    {
        return _history.TryRemove(sensorId, out _);
    }

    public IReadOnlyDictionary<Guid, int> GetHistorySummary()
    {
        return _history.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Count);
    }

    private IReadOnlyList<TelemetryHistoryReading> GetHistory(
        Guid sensorId)
    {
        return _history.TryGetValue(sensorId, out var history)
            ? history.GetSnapshot()
            : Array.Empty<TelemetryHistoryReading>();
    }

    private sealed class SensorHistory
    {
        private readonly object _sync = new();
        private readonly Queue<TelemetryHistoryReading> _readings = new();
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
                {
                    return _readings.Count;
                }
            }
        }

        public void Add(TelemetryHistoryReading reading)
        {
            lock (_sync)
            {
                _readings.Enqueue(reading);

                while (_readings.Count > _capacity)
                {
                    _readings.Dequeue();
                }
            }
        }

        public IReadOnlyList<TelemetryHistoryReading> GetSnapshot()
        {
            lock (_sync)
            {
                return _readings.ToArray();
            }
        }
    }
}

public sealed record TelemetryHistoryReading(
    Guid SensorId,
    double Value,
    DateTimeOffset ObservedAt);