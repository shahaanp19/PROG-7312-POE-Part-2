
using SmartX.Api.Services;

namespace SmartX.Tests.Services;

public class SensorRegistryServiceTests
{
    [Fact]
    public void Register_ValidSensor_AddsSensor()
    {
        var service = new SensorRegistryService();
        var sensorId = Guid.NewGuid();
        var sensor = new object();

        Assert.True(service.Register(sensorId, sensor));
        Assert.True(service.Contains(sensorId));
        Assert.Same(sensor, service.Get(sensorId));
        Assert.Single(service.GetAll());
    }

    [Fact]
    public void Register_RejectsEmptyIdAndDuplicateId()
    {
        var service = new SensorRegistryService();
        var sensorId = Guid.NewGuid();

        Assert.False(service.Register(Guid.Empty, new object()));
        Assert.True(service.Register(sensorId, new object()));
        Assert.False(service.Register(sensorId, new object()));
    }

    [Fact]
    public void Register_RejectsNullSensor()
    {
        var service = new SensorRegistryService();

        Assert.Throws<ArgumentNullException>(
            () => service.Register(Guid.NewGuid(), null!));
    }

    [Fact]
    public void AddConnection_CreatesUndirectedWeightedEdge()
    {
        var network = CreateRegisteredNetwork(2);
        var service = network.Service;
        var sensors = network.SensorIds;

        Assert.True(service.AddConnection(sensors[0], sensors[1], 2.5));

        Assert.Contains(sensors[1], service.GetNeighbors(sensors[0]));
        Assert.Contains(sensors[0], service.GetNeighbors(sensors[1]));

        var edge = Assert.Single(service.GetWeightedConnections());

        Assert.Equal(2.5, edge.Weight);
        Assert.True(
            (edge.FirstSensorId == sensors[0] &&
             edge.SecondSensorId == sensors[1]) ||
            (edge.FirstSensorId == sensors[1] &&
             edge.SecondSensorId == sensors[0]));
    }

    [Fact]
    public void AddConnection_DefaultWeightIsOne()
    {
        var network = CreateRegisteredNetwork(2);
        var service = network.Service;
        var sensors = network.SensorIds;

        Assert.True(service.AddConnection(sensors[0], sensors[1]));

        var edge = Assert.Single(service.GetWeightedConnections());

        Assert.Equal(1.0, edge.Weight);
    }

    [Fact]
    public void AddConnection_UpdatesExistingEdgeInBothDirections()
    {
        var network = CreateRegisteredNetwork(2);
        var service = network.Service;
        var sensors = network.SensorIds;

        Assert.True(service.AddConnection(sensors[0], sensors[1], 5));
        Assert.True(service.AddConnection(sensors[1], sensors[0], 2));

        var edge = Assert.Single(service.GetWeightedConnections());

        Assert.Equal(2, edge.Weight);
        Assert.Single(service.GetAllConnections());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void AddConnection_RejectsInvalidWeights(double weight)
    {
        var network = CreateRegisteredNetwork(2);
        var service = network.Service;
        var sensors = network.SensorIds;

        Assert.False(
            service.AddConnection(sensors[0], sensors[1], weight));

        Assert.Empty(service.GetWeightedConnections());
    }

    [Fact]
    public void AddConnection_AllowsZeroWeight()
    {
        var network = CreateRegisteredNetwork(2);
        var service = network.Service;
        var sensors = network.SensorIds;

        Assert.True(service.AddConnection(sensors[0], sensors[1], 0));

        Assert.Equal(
            0,
            Assert.Single(service.GetWeightedConnections()).Weight);
    }

    [Fact]
    public void AddConnection_RejectsInvalidSensorIdsAndSelfConnections()
    {
        var network = CreateRegisteredNetwork(1);
        var service = network.Service;
        var sensor = Assert.Single(network.SensorIds);

        Assert.False(service.AddConnection(Guid.Empty, sensor));
        Assert.False(service.AddConnection(sensor, Guid.Empty));
        Assert.False(service.AddConnection(sensor, sensor));
        Assert.False(service.AddConnection(sensor, Guid.NewGuid(), 1));
    }

    [Fact]
    public void RemoveConnection_RemovesBothDirections()
    {
        var network = CreateRegisteredNetwork(2);
        var service = network.Service;
        var sensors = network.SensorIds;

        service.AddConnection(sensors[0], sensors[1]);

        Assert.True(
            service.RemoveConnection(sensors[0], sensors[1]));

        Assert.Empty(service.GetNeighbors(sensors[0]));
        Assert.Empty(service.GetNeighbors(sensors[1]));
        Assert.Empty(service.GetWeightedConnections());

        Assert.False(
            service.RemoveConnection(sensors[0], sensors[1]));
    }

    [Fact]
    public void BreadthFirstSearch_VisitsReachableSensorsByLevels()
    {
        var network = CreateRegisteredNetwork(5);
        var service = network.Service;
        var sensors = network.SensorIds;

        //      0
        //     / \
        //    1   2
        //   / \
        //  3   4

        service.AddConnection(sensors[0], sensors[1]);
        service.AddConnection(sensors[0], sensors[2]);
        service.AddConnection(sensors[1], sensors[3]);
        service.AddConnection(sensors[1], sensors[4]);

        var result = service.BreadthFirstSearch(sensors[0]);

        Assert.Equal(5, result.Count);
        Assert.Equal(5, result.Distinct().Count());
        Assert.Equal(sensors[0], result[0]);

        Assert.Equal(
            sensors.Skip(1).Take(2).OrderBy(id => id),
            result.Skip(1).Take(2).OrderBy(id => id));

        Assert.Equal(
            sensors.Skip(3).Take(2).OrderBy(id => id),
            result.Skip(3).Take(2).OrderBy(id => id));
    }

    [Fact]
    public void DepthFirstSearch_VisitsEveryReachableSensorOnce()
    {
        var network = CreateRegisteredNetwork(5);
        var service = network.Service;
        var sensors = network.SensorIds;

        service.AddConnection(sensors[0], sensors[1]);
        service.AddConnection(sensors[0], sensors[2]);
        service.AddConnection(sensors[1], sensors[3]);
        service.AddConnection(sensors[1], sensors[4]);

        var result = service.DepthFirstSearch(sensors[0]);

        Assert.Equal(5, result.Count);
        Assert.Equal(5, result.Distinct().Count());

        foreach (var sensor in sensors)
            Assert.Contains(sensor, result);
    }

    [Fact]
    public void Searches_UnknownSensor_ReturnEmptyResults()
    {
        var service = new SensorRegistryService();
        var unknownSensor = Guid.NewGuid();

        Assert.Empty(service.BreadthFirstSearch(unknownSensor));
        Assert.Empty(service.DepthFirstSearch(unknownSensor));
    }

    [Fact]
    public void GetDisconnectedSensors_IdentifiesUnreachableSensors()
    {
        var network = CreateRegisteredNetwork(5);
        var service = network.Service;
        var sensors = network.SensorIds;

        service.AddConnection(sensors[0], sensors[1]);
        service.AddConnection(sensors[1], sensors[2]);
        service.AddConnection(sensors[3], sensors[4]);

        var disconnected =
            service.GetDisconnectedSensors(sensors[0]);

        Assert.Equal(
            new[] { sensors[3], sensors[4] }.OrderBy(id => id),
            disconnected.OrderBy(id => id));
    }

    [Fact]
    public void GetDisconnectedSensors_UnknownGatewayReturnsAllSensors()
    {
        var network = CreateRegisteredNetwork(3);
        var service = network.Service;

        var disconnected =
            service.GetDisconnectedSensors(Guid.NewGuid());

        Assert.Equal(
            network.SensorIds.OrderBy(id => id),
            disconnected.OrderBy(id => id));
    }

    [Fact]
    public void GetHopDistances_ReturnsMinimumUnweightedDistances()
    {
        var network = CreateRegisteredNetwork(5);
        var service = network.Service;
        var sensors = network.SensorIds;

        service.AddConnection(sensors[0], sensors[1]);
        service.AddConnection(sensors[0], sensors[2]);
        service.AddConnection(sensors[1], sensors[3]);
        service.AddConnection(sensors[2], sensors[3]);

        var distances = service.GetHopDistances(sensors[0]);

        Assert.Equal(0, distances[sensors[0]]);
        Assert.Equal(1, distances[sensors[1]]);
        Assert.Equal(1, distances[sensors[2]]);
        Assert.Equal(2, distances[sensors[3]]);
        Assert.False(distances.ContainsKey(sensors[4]));
    }

    [Fact]
    public void GetHopDistances_UnknownGatewayReturnsEmptyDictionary()
    {
        var service = new SensorRegistryService();

        Assert.Empty(service.GetHopDistances(Guid.NewGuid()));
    }

    [Fact]
    public void RemoveSensor_RemovesItsConnectionsAndRegistryEntry()
    {
        var network = CreateRegisteredNetwork(3);
        var service = network.Service;
        var sensors = network.SensorIds;

        service.AddConnection(sensors[0], sensors[1]);
        service.AddConnection(sensors[1], sensors[2]);

        Assert.True(service.Remove(sensors[1]));

        Assert.False(service.Contains(sensors[1]));
        Assert.Null(service.Get(sensors[1]));
        Assert.Empty(service.GetNeighbors(sensors[0]));
        Assert.Empty(service.GetNeighbors(sensors[2]));
        Assert.Equal(2, service.GetAll().Count);
        Assert.False(service.Remove(sensors[1]));
    }

    [Fact]
    public void PrimAndKruskal_ProduceSameMinimumSpanningTreeWeight()
    {
        var network = CreateRegisteredNetwork(5);
        var service = network.Service;
        var sensors = network.SensorIds;

        service.AddConnection(sensors[0], sensors[1], 4);
        service.AddConnection(sensors[0], sensors[2], 3);
        service.AddConnection(sensors[1], sensors[2], 1);
        service.AddConnection(sensors[1], sensors[3], 2);
        service.AddConnection(sensors[2], sensors[3], 4);
        service.AddConnection(sensors[3], sensors[4], 2);
        service.AddConnection(sensors[2], sensors[4], 5);

        var prim = service.GetMinimumSpanningTreePrim(sensors[0]);
        var kruskal = service.GetMinimumSpanningTreeKruskal();

        Assert.Equal(4, prim.Count);
        Assert.Equal(4, kruskal.Count);

        Assert.Equal(8, prim.Sum(edge => edge.Weight));
        Assert.Equal(8, kruskal.Sum(edge => edge.Weight));

        Assert.Equal(
            kruskal.Sum(edge => edge.Weight),
            prim.Sum(edge => edge.Weight));
    }

    [Fact]
    public void PrimAndKruskal_DisconnectedGraphProduceMinimumSpanningForest()
    {
        var network = CreateRegisteredNetwork(6);
        var service = network.Service;
        var sensors = network.SensorIds;

        // Component one: 0, 1, 2
        service.AddConnection(sensors[0], sensors[1], 2);
        service.AddConnection(sensors[1], sensors[2], 3);
        service.AddConnection(sensors[0], sensors[2], 10);

        // Component two: 3, 4
        service.AddConnection(sensors[3], sensors[4], 4);

        // Sensor 5 is isolated.

        var prim = service.GetMinimumSpanningTreePrim();
        var kruskal = service.GetMinimumSpanningTreeKruskal();

        // Six vertices across three components require three forest edges.
        Assert.Equal(3, prim.Count);
        Assert.Equal(3, kruskal.Count);

        Assert.Equal(9, prim.Sum(edge => edge.Weight));
        Assert.Equal(9, kruskal.Sum(edge => edge.Weight));
    }

    [Fact]
    public void PrimAndKruskal_EmptyGraphReturnEmptyForests()
    {
        var service = new SensorRegistryService();

        Assert.Empty(service.GetMinimumSpanningTreePrim());
        Assert.Empty(service.GetMinimumSpanningTreeKruskal());
    }

    [Fact]
    public void PrimAndKruskal_SingleSensorReturnEmptyForests()
    {
        var network = CreateRegisteredNetwork(1);
        var service = network.Service;

        Assert.Empty(service.GetMinimumSpanningTreePrim());
        Assert.Empty(service.GetMinimumSpanningTreeKruskal());
    }

    [Fact]
    public void Prim_UnknownStartingSensorStillProcessesAllComponents()
    {
        var network = CreateRegisteredNetwork(3);
        var service = network.Service;
        var sensors = network.SensorIds;

        service.AddConnection(sensors[0], sensors[1], 2);
        service.AddConnection(sensors[1], sensors[2], 3);

        var result =
            service.GetMinimumSpanningTreePrim(Guid.NewGuid());

        Assert.Equal(2, result.Count);
        Assert.Equal(5, result.Sum(edge => edge.Weight));
    }

    [Fact]
    public void GetAllConnections_ReturnsEachUndirectedEdgeOnce()
    {
        var network = CreateRegisteredNetwork(3);
        var service = network.Service;
        var sensors = network.SensorIds;

        service.AddConnection(sensors[0], sensors[1]);
        service.AddConnection(sensors[1], sensors[2]);

        var connections = service.GetAllConnections();

        Assert.Equal(2, connections.Count);
        Assert.Equal(2, connections.Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentRegistrationAndConnectionOperations_RemainConsistent()
    {
        const int sensorCount = 50;

        var service = new SensorRegistryService();
        var sensors = Enumerable.Range(0, sensorCount)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        await Task.WhenAll(sensors.Select(sensorId =>
            Task.Run(() => service.Register(sensorId, new object()))));

        Assert.Equal(sensorCount, service.GetAll().Count);

        var connectionTasks = Enumerable.Range(0, sensorCount - 1)
            .Select(index => Task.Run(() =>
                service.AddConnection(
                    sensors[index],
                    sensors[index + 1],
                    index + 1)));

        await Task.WhenAll(connectionTasks);

        Assert.Equal(
            sensorCount - 1,
            service.GetWeightedConnections().Count);

        Assert.Equal(
            sensorCount,
            service.BreadthFirstSearch(sensors[0]).Count);

        Assert.Empty(service.GetDisconnectedSensors(sensors[0]));
    }

    private static TestNetwork CreateRegisteredNetwork(int count)
    {
        var service = new SensorRegistryService();
        var sensorIds = new Guid[count];

        for (var i = 0; i < count; i++)
        {
            sensorIds[i] = Guid.NewGuid();

            Assert.True(
                service.Register(sensorIds[i], new object()));
        }

        return new TestNetwork(service, sensorIds);
    }

    private sealed record TestNetwork(
        SensorRegistryService Service,
        Guid[] SensorIds);
}
