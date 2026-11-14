
namespace SmartX.Api.Services;

/// <summary>
/// Maintains registered sensors and their weighted wireless mesh connections.
/// Sensors are vertices; wireless connections are undirected weighted edges.
/// </summary>
public class SensorRegistryService
{
    private readonly Dictionary<Guid, object> _sensors = new();
    private readonly Dictionary<Guid, Dictionary<Guid, double>> _connections = new();
    private readonly object _syncLock = new();

    public IReadOnlyCollection<object> GetAll()
    {
        lock (_syncLock)
            return _sensors.Values.ToList().AsReadOnly();
    }

    public bool Contains(Guid sensorId)
    {
        if (sensorId == Guid.Empty)
            return false;

        lock (_syncLock)
            return _sensors.ContainsKey(sensorId);
    }

    public bool Register(Guid sensorId, object sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        if (sensorId == Guid.Empty)
            return false;

        lock (_syncLock)
        {
            if (_sensors.ContainsKey(sensorId))
                return false;

            _sensors.Add(sensorId, sensor);
            _connections.Add(sensorId, new Dictionary<Guid, double>());

            return true;
        }
    }

    public bool Remove(Guid sensorId)
    {
        if (sensorId == Guid.Empty)
            return false;

        lock (_syncLock)
        {
            if (!_sensors.Remove(sensorId))
                return false;

            _connections.Remove(sensorId);

            foreach (var neighbors in _connections.Values)
                neighbors.Remove(sensorId);

            return true;
        }
    }

    public object? Get(Guid sensorId)
    {
        if (sensorId == Guid.Empty)
            return null;

        lock (_syncLock)
        {
            return _sensors.TryGetValue(sensorId, out var sensor)
                ? sensor
                : null;
        }
    }

    /// <summary>
    /// Adds a bidirectional connection with a default routing cost of 1.
    /// </summary>
    public bool AddConnection(Guid firstSensorId, Guid secondSensorId) =>
        AddConnection(firstSensorId, secondSensorId, 1.0);

    /// <summary>
    /// Adds or updates a bidirectional wireless connection.
    /// Lower weights represent lower routing costs.
    /// Weights must be finite and non-negative.
    /// </summary>
    public bool AddConnection(
        Guid firstSensorId,
        Guid secondSensorId,
        double weight)
    {
        if (firstSensorId == Guid.Empty ||
            secondSensorId == Guid.Empty ||
            firstSensorId == secondSensorId ||
            !double.IsFinite(weight) ||
            weight < 0)
        {
            return false;
        }

        lock (_syncLock)
        {
            if (!_sensors.ContainsKey(firstSensorId) ||
                !_sensors.ContainsKey(secondSensorId))
            {
                return false;
            }

            _connections[firstSensorId][secondSensorId] = weight;
            _connections[secondSensorId][firstSensorId] = weight;

            return true;
        }
    }

    public bool RemoveConnection(
        Guid firstSensorId,
        Guid secondSensorId)
    {
        lock (_syncLock)
        {
            if (!_connections.TryGetValue(firstSensorId, out var firstNeighbors) ||
                !_connections.TryGetValue(secondSensorId, out var secondNeighbors))
            {
                return false;
            }

            var removedFirst = firstNeighbors.Remove(secondSensorId);
            var removedSecond = secondNeighbors.Remove(firstSensorId);

            return removedFirst || removedSecond;
        }
    }

    public IReadOnlyCollection<Guid> GetNeighbors(Guid sensorId)
    {
        lock (_syncLock)
        {
            return _connections.TryGetValue(sensorId, out var neighbors)
                ? neighbors.Keys.OrderBy(id => id).ToList().AsReadOnly()
                : Array.Empty<Guid>();
        }
    }

    /// <summary>
    /// Performs breadth-first traversal from the specified sensor.
    /// Returns only vertices reachable from the starting vertex.
    ///
    /// Time complexity: O(V + E).
    /// Space complexity: O(V).
    /// </summary>
    public IReadOnlyList<Guid> BreadthFirstSearch(Guid startSensorId)
    {
        lock (_syncLock)
        {
            if (!_connections.ContainsKey(startSensorId))
                return Array.Empty<Guid>();

            var visited = new HashSet<Guid> { startSensorId };
            var result = new List<Guid>();
            var queue = new Queue<Guid>();

            queue.Enqueue(startSensorId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);

                foreach (var neighbor in _connections[current].Keys)
                {
                    if (visited.Add(neighbor))
                        queue.Enqueue(neighbor);
                }
            }

            return result.AsReadOnly();
        }
    }

    /// <summary>
    /// Performs iterative depth-first traversal.
    /// Returns only vertices reachable from the starting vertex.
    ///
    /// Time complexity: O(V + E).
    /// Space complexity: O(V).
    /// </summary>
    public IReadOnlyList<Guid> DepthFirstSearch(Guid startSensorId)
    {
        lock (_syncLock)
        {
            if (!_connections.ContainsKey(startSensorId))
                return Array.Empty<Guid>();

            var visited = new HashSet<Guid>();
            var result = new List<Guid>();
            var stack = new Stack<Guid>();

            stack.Push(startSensorId);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                if (!visited.Add(current))
                    continue;

                result.Add(current);

                foreach (var neighbor in _connections[current].Keys)
                {
                    if (!visited.Contains(neighbor))
                        stack.Push(neighbor);
                }
            }

            return result.AsReadOnly();
        }
    }

    /// <summary>
    /// Returns all registered sensors that cannot be reached from the gateway.
    /// If the gateway is not registered, every registered sensor is disconnected.
    ///
    /// Time complexity: O(V + E).
    /// Space complexity: O(V).
    /// </summary>
    public IReadOnlyList<Guid> GetDisconnectedSensors(Guid gatewaySensorId)
    {
        lock (_syncLock)
        {
            if (!_connections.ContainsKey(gatewaySensorId))
            {
                return _sensors.Keys.ToList().AsReadOnly();
            }

            var reachable = new HashSet<Guid>();
            var queue = new Queue<Guid>();

            reachable.Add(gatewaySensorId);
            queue.Enqueue(gatewaySensorId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                foreach (var neighbor in _connections[current].Keys)
                {
                    if (reachable.Add(neighbor))
                        queue.Enqueue(neighbor);
                }
            }

            return _sensors.Keys
                .Where(id => !reachable.Contains(id))
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>
    /// Calculates minimum hop distances from a gateway using BFS.
    /// Disconnected sensors are omitted from the returned dictionary.
    ///
    /// Time complexity: O(V + E).
    /// Space complexity: O(V).
    /// </summary>
    public IReadOnlyDictionary<Guid, int> GetHopDistances(
        Guid gatewaySensorId)
    {
        lock (_syncLock)
        {
            var distances = new Dictionary<Guid, int>();

            if (!_connections.ContainsKey(gatewaySensorId))
                return distances;

            var queue = new Queue<Guid>();

            distances[gatewaySensorId] = 0;
            queue.Enqueue(gatewaySensorId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                foreach (var neighbor in _connections[current].Keys)
                {
                    if (distances.ContainsKey(neighbor))
                        continue;

                    distances[neighbor] = distances[current] + 1;
                    queue.Enqueue(neighbor);
                }
            }

            return distances;
        }
    }

    /// <summary>
    /// Returns each undirected connection exactly once.
    /// </summary>
    public IReadOnlyList<(Guid FirstSensorId, Guid SecondSensorId)>
        GetAllConnections()
    {
        lock (_syncLock)
        {
            return GetUniqueEdges()
                .Select(edge => (
                    edge.FirstSensorId,
                    edge.SecondSensorId))
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>
    /// Returns each undirected weighted connection exactly once.
    /// </summary>
    public IReadOnlyList<WeightedSensorConnection> GetWeightedConnections()
    {
        lock (_syncLock)
            return GetUniqueEdges().AsReadOnly();
    }

    /// <summary>
    /// Calculates a minimum spanning forest using Prim's algorithm.
    /// Disconnected graphs produce one minimum spanning tree per component.
    ///
    /// Time complexity: O(V + E log E).
    /// Space complexity: O(V + E).
    /// </summary>
    public IReadOnlyList<WeightedSensorConnection>
        GetMinimumSpanningTreePrim(Guid? startSensorId = null)
    {
        lock (_syncLock)
        {
            var result = new List<WeightedSensorConnection>();
            var visited = new HashSet<Guid>();

            var startingVertices = _sensors.Keys
                .OrderBy(id => id)
                .ToList();

            if (startSensorId.HasValue &&
                _sensors.ContainsKey(startSensorId.Value))
            {
                startingVertices.Remove(startSensorId.Value);
                startingVertices.Insert(0, startSensorId.Value);
            }

            foreach (var start in startingVertices)
            {
                if (!visited.Add(start))
                    continue;

                var candidates =
                    new PriorityQueue<
                        PrimCandidate,
                        (double Weight, Guid First, Guid Second)>();

                AddCandidateEdges(start, visited, candidates);

                while (candidates.TryDequeue(
                           out var candidate,
                           out _))
                {
                    if (!visited.Add(candidate.Destination))
                        continue;

                    result.Add(candidate.Edge);

                    AddCandidateEdges(
                        candidate.Destination,
                        visited,
                        candidates);
                }
            }

            return result.AsReadOnly();
        }
    }

    /// <summary>
    /// Calculates a minimum spanning forest using Kruskal's algorithm
    /// and a disjoint-set union structure with path compression and rank.
    ///
    /// Time complexity: O(E log E + V alpha(V)).
    /// Space complexity: O(V + E).
    /// </summary>
    public IReadOnlyList<WeightedSensorConnection>
        GetMinimumSpanningTreeKruskal()
    {
        lock (_syncLock)
        {
            var edges = GetUniqueEdges()
                .OrderBy(edge => edge.Weight)
                .ThenBy(edge => edge.FirstSensorId)
                .ThenBy(edge => edge.SecondSensorId)
                .ToList();

            var parent = _sensors.Keys
                .ToDictionary(id => id, id => id);

            var rank = _sensors.Keys
                .ToDictionary(id => id, _ => 0);

            var result = new List<WeightedSensorConnection>();

            foreach (var edge in edges)
            {
                var firstRoot = Find(parent, edge.FirstSensorId);
                var secondRoot = Find(parent, edge.SecondSensorId);

                if (firstRoot == secondRoot)
                    continue;

                Union(parent, rank, firstRoot, secondRoot);
                result.Add(edge);
            }

            return result.AsReadOnly();
        }
    }

    private void AddCandidateEdges(
        Guid sensorId,
        HashSet<Guid> visited,
        PriorityQueue<
            PrimCandidate,
            (double Weight, Guid First, Guid Second)> queue)
    {
        foreach (var neighbor in
                 _connections[sensorId].OrderBy(entry => entry.Key))
        {
            if (visited.Contains(neighbor.Key))
                continue;

            var first = sensorId.CompareTo(neighbor.Key) < 0
                ? sensorId
                : neighbor.Key;

            var second = sensorId.CompareTo(neighbor.Key) < 0
                ? neighbor.Key
                : sensorId;

            var edge = new WeightedSensorConnection(
                first,
                second,
                neighbor.Value);

            var candidate = new PrimCandidate(
                edge,
                neighbor.Key);

            queue.Enqueue(
                candidate,
                (edge.Weight, edge.FirstSensorId, edge.SecondSensorId));
        }
    }

    private List<WeightedSensorConnection> GetUniqueEdges()
    {
        var edges = new List<WeightedSensorConnection>();

        foreach (var entry in _connections)
        {
            foreach (var neighbor in entry.Value)
            {
                if (entry.Key.CompareTo(neighbor.Key) >= 0)
                    continue;

                edges.Add(new WeightedSensorConnection(
                    entry.Key,
                    neighbor.Key,
                    neighbor.Value));
            }
        }

        return edges
            .OrderBy(edge => edge.FirstSensorId)
            .ThenBy(edge => edge.SecondSensorId)
            .ToList();
    }

    private static Guid Find(
        Dictionary<Guid, Guid> parent,
        Guid sensorId)
    {
        if (parent[sensorId] != sensorId)
        {
            parent[sensorId] = Find(
                parent,
                parent[sensorId]);
        }

        return parent[sensorId];
    }

    private static void Union(
        Dictionary<Guid, Guid> parent,
        Dictionary<Guid, int> rank,
        Guid firstRoot,
        Guid secondRoot)
    {
        if (rank[firstRoot] < rank[secondRoot])
        {
            parent[firstRoot] = secondRoot;
        }
        else if (rank[firstRoot] > rank[secondRoot])
        {
            parent[secondRoot] = firstRoot;
        }
        else
        {
            parent[secondRoot] = firstRoot;
            rank[firstRoot]++;
        }
    }

    private sealed record PrimCandidate(
        WeightedSensorConnection Edge,
        Guid Destination);
}

/// <summary>
/// Represents an undirected weighted connection between two sensors.
/// </summary>
public sealed record WeightedSensorConnection(
    Guid FirstSensorId,
    Guid SecondSensorId,
    double Weight);
