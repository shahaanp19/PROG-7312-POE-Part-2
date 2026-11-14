
using System.Collections.Concurrent;

namespace SmartX.Api.Services;

public sealed class TelemetryHistoryService
{
    private const int DefaultMaxReadingsPerSensor = 10_000;

    private readonly ConcurrentDictionary<Guid, SensorHistory> _history = new();
    private readonly int _maxReadingsPerSensor;

    public TelemetryHistoryService() : this(DefaultMaxReadingsPerSensor)
    {
    }

    public TelemetryHistoryService(int maxReadingsPerSensor)
    {
        if (maxReadingsPerSensor <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxReadingsPerSensor));

        _maxReadingsPerSensor = maxReadingsPerSensor;
    }

    public void AddReading(Guid sensorId, double value) =>
        AddReading(sensorId, value, DateTimeOffset.UtcNow);

    public void AddReading(Guid sensorId, double value, DateTimeOffset observedAt) =>
        AddReading(sensorId, value, string.Empty, string.Empty, 0, 0, observedAt);

    public void AddReading(Guid sensorId, double value, string metricName, string unit) =>
        AddReading(sensorId, value, metricName, unit, 0, 0, DateTimeOffset.UtcNow);

    public void AddReading(
        Guid sensorId,
        double value,
        string metricName,
        string unit,
        DateTimeOffset observedAt) =>
        AddReading(sensorId, value, metricName, unit, 0, 0, observedAt);

    public void AddReading(
        Guid sensorId,
        double value,
        string metricName,
        string unit,
        double warningThreshold,
        double criticalThreshold) =>
        AddReading(
            sensorId,
            value,
            metricName,
            unit,
            warningThreshold,
            criticalThreshold,
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
            throw new ArgumentException(
                "Sensor ID cannot be empty.",
                nameof(sensorId));

        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));

        if (!double.IsFinite(warningThreshold) ||
            !double.IsFinite(criticalThreshold))
        {
            throw new ArgumentOutOfRangeException(nameof(warningThreshold));
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
            throw new ArgumentOutOfRangeException(nameof(count));

        var snapshot = GetHistory(sensorId);
        var start = Math.Max(0, snapshot.Count - count);

        return snapshot.Skip(start).ToArray();
    }

    /// <summary>
    /// Retrieves readings in an inclusive timestamp range.
    /// Expected complexity: O(log n + k), where k is the number of results.
    /// </summary>
    public IReadOnlyList<TelemetryHistoryReading> GetReadingsBetween(
        Guid sensorId,
        DateTimeOffset from,
        DateTimeOffset to,
        int? limit = null)
    {
        if (from > to)
            throw new ArgumentException(
                "The start time cannot be later than the end time.");

        if (limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));

        if (!_history.TryGetValue(sensorId, out var history))
            return Array.Empty<TelemetryHistoryReading>();

        return history.GetRange(
            from.ToUniversalTime(),
            to.ToUniversalTime(),
            limit);
    }

    public IReadOnlyList<TelemetryHistoryReading> GetAllTimestampedReadings(
        int countPerSensor = 100)
    {
        if (countPerSensor <= 0)
            throw new ArgumentOutOfRangeException(nameof(countPerSensor));

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

    /// <summary>
    /// Returns structural diagnostics for the sensor's AVL tree.
    /// An unknown sensor returns an empty, balanced tree.
    /// </summary>
    public AvlTreeDiagnostics GetTreeDiagnostics(Guid sensorId)
    {
        if (!_history.TryGetValue(sensorId, out var history))
            return AvlTreeDiagnostics.Empty;

        return history.GetDiagnostics();
    }

    public TelemetryTrendSummary? GetTrendSummary(
        Guid sensorId,
        int count = 20)
    {
        if (count < 2)
            throw new ArgumentOutOfRangeException(nameof(count));

        var readings = GetTimestampedReadings(sensorId, count);

        if (readings.Count < 2)
            return null;

        var first = readings[0];
        var last = readings[^1];

        var elapsedMinutes =
            (last.ObservedAt - first.ObservedAt).TotalMinutes;

        if (elapsedMinutes <= 0)
            return null;

        var values = readings.Select(reading => reading.Value).ToArray();
        var change = last.Value - first.Value;
        var slope = change / elapsedMinutes;

        if (!double.IsFinite(slope))
            return null;

        var mean = values.Average();

        var variance = values.Average(
            value => (value - mean) * (value - mean));

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
        if (rows <= 0 || columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows));

        var readings = GetReadings(sensorId);
        var matrix = new double[rows, columns];

        var capacity = (long)rows * columns;
        var startIndex = Math.Max(0L, readings.Count - capacity);

        for (long index = startIndex;
             index < readings.Count &&
             index - startIndex < capacity;
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

    public DateTimeOffset? GetLatestObservedAt(Guid sensorId) =>
        _history.TryGetValue(sensorId, out var history)
            ? history.GetLatestObservedAt()
            : null;

    public bool RemoveHistory(Guid sensorId) =>
        _history.TryRemove(sensorId, out _);

    private IReadOnlyList<TelemetryHistoryReading> GetHistory(Guid sensorId) =>
        _history.TryGetValue(sensorId, out var history)
            ? history.GetSnapshot()
            : Array.Empty<TelemetryHistoryReading>();

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
        private readonly int _capacity;

        private Node? _root;
        private int _count;
        private long _sequence;

        private long _llRotations;
        private long _rrRotations;
        private long _lrRotations;
        private long _rlRotations;

        public SensorHistory(int capacity)
        {
            _capacity = capacity;
        }

        public int Count
        {
            get
            {
                lock (_sync)
                    return _count;
            }
        }

        public void Add(TelemetryHistoryReading reading)
        {
            lock (_sync)
            {
                var key = new TreeKey(
                    reading.ObservedAt,
                    ++_sequence);

                _root = Insert(_root, key, reading);
                _count++;

                if (_count > _capacity)
                {
                    _root = RemoveMinimum(_root);
                    _count--;
                }
            }
        }

        public IReadOnlyList<TelemetryHistoryReading> GetSnapshot()
        {
            lock (_sync)
            {
                var result = new List<TelemetryHistoryReading>(_count);
                TraverseInOrder(_root, result);
                return result.ToArray();
            }
        }

        public IReadOnlyList<TelemetryHistoryReading> GetRange(
            DateTimeOffset from,
            DateTimeOffset to,
            int? limit)
        {
            lock (_sync)
            {
                var result = new List<TelemetryHistoryReading>(
                    limit.HasValue
                        ? Math.Min(limit.Value, _count)
                        : Math.Min(_count, 256));

                CollectRange(_root, from, to, limit, result);

                return result.ToArray();
            }
        }

        public DateTimeOffset? GetLatestObservedAt()
        {
            lock (_sync)
            {
                if (_root is null)
                    return null;

                var current = _root;

                while (current.Right is not null)
                    current = current.Right;

                return current.Reading.ObservedAt;
            }
        }

        public AvlTreeDiagnostics GetDiagnostics()
        {
            lock (_sync)
            {
                var structure = InspectTree(_root);

                return new AvlTreeDiagnostics(
                    structure.Height,
                    structure.NodeCount,
                    structure.IsBalanced,
                    _llRotations,
                    _rrRotations,
                    _lrRotations,
                    _rlRotations);
            }
        }

        private static void CollectRange(
            Node? node,
            DateTimeOffset from,
            DateTimeOffset to,
            int? limit,
            List<TelemetryHistoryReading> result)
        {
            if (node is null ||
                (limit.HasValue && result.Count >= limit.Value))
            {
                return;
            }

            var timestamp = node.Reading.ObservedAt;

            if (timestamp >= from)
                CollectRange(node.Left, from, to, limit, result);

            if (timestamp >= from &&
                timestamp <= to &&
                (!limit.HasValue || result.Count < limit.Value))
            {
                result.Add(node.Reading);
            }

            if (timestamp <= to &&
                (!limit.HasValue || result.Count < limit.Value))
            {
                CollectRange(node.Right, from, to, limit, result);
            }
        }

        private static void TraverseInOrder(
            Node? node,
            List<TelemetryHistoryReading> result)
        {
            if (node is null)
                return;

            TraverseInOrder(node.Left, result);
            result.Add(node.Reading);
            TraverseInOrder(node.Right, result);
        }

        private Node Insert(
            Node? node,
            TreeKey key,
            TelemetryHistoryReading reading)
        {
            if (node is null)
                return new Node(key, reading);

            if (key.CompareTo(node.Key) < 0)
                node.Left = Insert(node.Left, key, reading);
            else
                node.Right = Insert(node.Right, key, reading);

            return Balance(node);
        }

        private Node? RemoveMinimum(Node? node)
        {
            if (node is null)
                return null;

            if (node.Left is null)
                return node.Right;

            node.Left = RemoveMinimum(node.Left);

            return Balance(node);
        }

        private Node Balance(Node node)
        {
            UpdateHeight(node);

            var balanceFactor =
                Height(node.Left) - Height(node.Right);

            if (balanceFactor > 1)
            {
                var leftBalanceFactor =
                    Height(node.Left!.Left) -
                    Height(node.Left.Right);

                if (leftBalanceFactor >= 0)
                {
                    _llRotations++;
                    return RotateRight(node);
                }

                _lrRotations++;
                node.Left = RotateLeft(node.Left);

                return RotateRight(node);
            }

            if (balanceFactor < -1)
            {
                var rightBalanceFactor =
                    Height(node.Right!.Left) -
                    Height(node.Right.Right);

                if (rightBalanceFactor <= 0)
                {
                    _rrRotations++;
                    return RotateLeft(node);
                }

                _rlRotations++;
                node.Right = RotateRight(node.Right);

                return RotateLeft(node);
            }

            return node;
        }

        private static Node RotateRight(Node root)
        {
            var pivot = root.Left!;

            root.Left = pivot.Right;
            pivot.Right = root;

            UpdateHeight(root);
            UpdateHeight(pivot);

            return pivot;
        }

        private static Node RotateLeft(Node root)
        {
            var pivot = root.Right!;

            root.Right = pivot.Left;
            pivot.Left = root;

            UpdateHeight(root);
            UpdateHeight(pivot);

            return pivot;
        }

        private static int Height(Node? node) =>
            node?.Height ?? 0;

        private static void UpdateHeight(Node node)
        {
            node.Height = 1 + Math.Max(
                Height(node.Left),
                Height(node.Right));
        }

        private static TreeInspection InspectTree(Node? node)
        {
            if (node is null)
                return new TreeInspection(0, 0, true);

            var left = InspectTree(node.Left);
            var right = InspectTree(node.Right);

            var height = 1 + Math.Max(
                left.Height,
                right.Height);

            var nodeCount = 1 +
                left.NodeCount +
                right.NodeCount;

            var balanced =
                left.IsBalanced &&
                right.IsBalanced &&
                Math.Abs(left.Height - right.Height) <= 1 &&
                node.Height == height;

            return new TreeInspection(
                height,
                nodeCount,
                balanced);
        }

        private readonly record struct TreeInspection(
            int Height,
            int NodeCount,
            bool IsBalanced);

        private readonly record struct TreeKey(
            DateTimeOffset Timestamp,
            long Sequence) : IComparable<TreeKey>
        {
            public int CompareTo(TreeKey other)
            {
                var comparison =
                    Timestamp.CompareTo(other.Timestamp);

                return comparison != 0
                    ? comparison
                    : Sequence.CompareTo(other.Sequence);
            }
        }

        private sealed class Node
        {
            public TreeKey Key { get; }

            public TelemetryHistoryReading Reading { get; }

            public Node? Left { get; set; }

            public Node? Right { get; set; }

            public int Height { get; set; } = 1;

            public Node(
                TreeKey key,
                TelemetryHistoryReading reading)
            {
                Key = key;
                Reading = reading;
            }
        }
    }
}

public sealed record AvlTreeDiagnostics(
    int Height,
    int NodeCount,
    bool IsBalanced,
    long LeftLeftRotations,
    long RightRightRotations,
    long LeftRightRotations,
    long RightLeftRotations)
{
    public long TotalRotations =>
        LeftLeftRotations +
        RightRightRotations +
        LeftRightRotations +
        RightLeftRotations;

    public static AvlTreeDiagnostics Empty { get; } =
        new(0, 0, true, 0, 0, 0, 0);
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
