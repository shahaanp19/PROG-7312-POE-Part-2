
using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/sensors")]
public class SensorController : ControllerBase
{
    private readonly SensorRegistryService _registry;

    public SensorController(SensorRegistryService registry)
    {
        _registry = registry;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        return Ok(_registry.GetAll());
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "Healthy",
            service = "Sensor Mesh API",
            timestampUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Returns the complete weighted mesh topology.
    /// </summary>
    [HttpGet("topology")]
    public IActionResult GetTopology()
    {
        var sensors = _registry.GetAll();
        var connections = _registry.GetWeightedConnections();

        return Ok(new
        {
            sensorCount = sensors.Count,
            connectionCount = connections.Count,
            sensors,
            connections
        });
    }

    [HttpGet("{sensorId:guid}")]
    public IActionResult Get(Guid sensorId)
    {
        var sensor = _registry.Get(sensorId);

        return sensor is null ? NotFound() : Ok(sensor);
    }

    [HttpGet("{sensorId:guid}/neighbors")]
    public IActionResult GetNeighbors(Guid sensorId)
    {
        if (!_registry.Contains(sensorId))
            return NotFound(new { message = "Sensor not found." });

        return Ok(_registry.GetNeighbors(sensorId));
    }

    /// <summary>
    /// Creates or updates an undirected weighted mesh connection.
    /// </summary>
    [HttpPost("connections")]
    public IActionResult AddConnection(
        [FromBody] MeshConnectionRequest request)
    {
        if (request.FirstSensorId == Guid.Empty ||
            request.SecondSensorId == Guid.Empty ||
            request.FirstSensorId == request.SecondSensorId)
        {
            return BadRequest(new
            {
                message = "Provide two different valid sensor IDs."
            });
        }

        if (!double.IsFinite(request.Weight) || request.Weight < 0)
        {
            return BadRequest(new
            {
                message = "Connection weight must be finite and non-negative."
            });
        }

        if (!_registry.Contains(request.FirstSensorId) ||
            !_registry.Contains(request.SecondSensorId))
        {
            return NotFound(new
            {
                message = "Both sensors must be registered before connecting them."
            });
        }

        if (!_registry.AddConnection(
                request.FirstSensorId,
                request.SecondSensorId,
                request.Weight))
        {
            return Conflict(new
            {
                message = "The connection could not be created."
            });
        }

        return Ok(new
        {
            message = "Wireless connection created.",
            request.FirstSensorId,
            request.SecondSensorId,
            request.Weight
        });
    }

    [HttpDelete("connections")]
    public IActionResult RemoveConnection(
        [FromBody] MeshConnectionRequest request)
    {
        return _registry.RemoveConnection(
            request.FirstSensorId,
            request.SecondSensorId)
            ? NoContent()
            : NotFound(new
            {
                message = "Wireless connection not found."
            });
    }

    [HttpGet("mesh/{gatewayId:guid}/bfs")]
    public IActionResult BreadthFirstSearch(Guid gatewayId)
    {
        if (!_registry.Contains(gatewayId))
            return NotFound(new { message = "Gateway sensor not found." });

        return Ok(new
        {
            gatewayId,
            traversal = _registry.BreadthFirstSearch(gatewayId)
        });
    }

    [HttpGet("mesh/{gatewayId:guid}/dfs")]
    public IActionResult DepthFirstSearch(Guid gatewayId)
    {
        if (!_registry.Contains(gatewayId))
            return NotFound(new { message = "Gateway sensor not found." });

        return Ok(new
        {
            gatewayId,
            traversal = _registry.DepthFirstSearch(gatewayId)
        });
    }

    /// <summary>
    /// Returns reachable sensors, disconnected sensors and minimum hop distances.
    /// </summary>
    [HttpGet("mesh/{gatewayId:guid}/status")]
    public IActionResult GetMeshStatus(Guid gatewayId)
    {
        if (!_registry.Contains(gatewayId))
            return NotFound(new { message = "Gateway sensor not found." });

        var hopDistances = _registry.GetHopDistances(gatewayId);
        var disconnected = _registry.GetDisconnectedSensors(gatewayId);

        return Ok(new
        {
            gatewayId,
            totalSensors = _registry.GetAll().Count,
            reachableSensors = hopDistances.Count,
            disconnectedSensors = disconnected,
            hopDistances,
            networkConnected = disconnected.Count == 0
        });
    }

    /// <summary>
    /// Calculates a minimum spanning forest using Prim's algorithm.
    /// An optional gateway ID determines the preferred starting component.
    /// </summary>
    [HttpGet("mesh/mst/prim")]
    public IActionResult GetPrimMinimumSpanningTree(
        [FromQuery] Guid? gatewayId = null)
    {
        if (gatewayId.HasValue && !_registry.Contains(gatewayId.Value))
        {
            return NotFound(new
            {
                message = "Starting gateway sensor not found."
            });
        }

        var edges = _registry.GetMinimumSpanningTreePrim(gatewayId);

        return Ok(new
        {
            algorithm = "Prim",
            edgeCount = edges.Count,
            totalWeight = edges.Sum(edge => edge.Weight),
            connections = edges
        });
    }

    /// <summary>
    /// Calculates a minimum spanning forest using Kruskal's algorithm.
    /// </summary>
    [HttpGet("mesh/mst/kruskal")]
    public IActionResult GetKruskalMinimumSpanningTree()
    {
        var edges = _registry.GetMinimumSpanningTreeKruskal();

        return Ok(new
        {
            algorithm = "Kruskal",
            edgeCount = edges.Count,
            totalWeight = edges.Sum(edge => edge.Weight),
            connections = edges
        });
    }

    [HttpDelete("{sensorId:guid}")]
    public IActionResult Remove(Guid sensorId)
    {
        return _registry.Remove(sensorId)
            ? NoContent()
            : NotFound();
    }
}

public class MeshConnectionRequest
{
    public Guid FirstSensorId { get; set; }

    public Guid SecondSensorId { get; set; }

    public double Weight { get; set; } = 1.0;
}
