
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

    [HttpGet("topology")]
    public IActionResult GetTopology()
    {
        var sensors = _registry.GetAll();

        var connections = _registry.GetAllConnections()
            .Select(edge => new
            {
                source = edge.FirstSensorId,
                target = edge.SecondSensorId
            });

        return Ok(new
        {
            sensorCount = sensors.Count,
            connectionCount = connections.Count(),
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

    [HttpPost("connections")]
    public IActionResult AddConnection([FromBody] MeshConnectionRequest request)
    {
        if (request.FirstSensorId == Guid.Empty ||
            request.SecondSensorId == Guid.Empty ||
            request.FirstSensorId == request.SecondSensorId)
        {
            return BadRequest(new { message = "Provide two different valid sensor IDs." });
        }

        if (!_registry.Contains(request.FirstSensorId) ||
            !_registry.Contains(request.SecondSensorId))
        {
            return NotFound(new { message = "Both sensors must be registered before connecting them." });
        }

        if (!_registry.AddConnection(request.FirstSensorId, request.SecondSensorId))
            return Conflict(new { message = "The connection could not be created." });

        return Ok(new
        {
            message = "Wireless connection created.",
            request.FirstSensorId,
            request.SecondSensorId
        });
    }

    [HttpDelete("connections")]
    public IActionResult RemoveConnection([FromBody] MeshConnectionRequest request)
    {
        return _registry.RemoveConnection(
            request.FirstSensorId,
            request.SecondSensorId)
            ? NoContent()
            : NotFound(new { message = "Wireless connection not found." });
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
}
