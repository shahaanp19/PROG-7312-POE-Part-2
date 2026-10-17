
using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/telemetry-history")]
public sealed class TelemetryHistoryController : ControllerBase
{
    private readonly TelemetryHistoryService _historyService;

    public TelemetryHistoryController(
        TelemetryHistoryService historyService)
    {
        _historyService = historyService
            ?? throw new ArgumentNullException(nameof(historyService));
    }

    // Preserve the existing numeric history endpoint.
    [HttpGet("{sensorId:guid}")]
    public ActionResult<IReadOnlyList<double>> Get(Guid sensorId)
    {
        return Ok(_historyService.GetReadings(sensorId));
    }

    // Preserve the existing matrix endpoint.
    [HttpGet("{sensorId:guid}/matrix")]
    public ActionResult<double[,]> GetMatrix(
        Guid sensorId,
        [FromQuery] int rows = 5,
        [FromQuery] int columns = 5)
    {
        if (rows <= 0 || columns <= 0)
        {
            return BadRequest(
                "Rows and columns must be greater than zero.");
        }

        return Ok(_historyService.GetMatrix(sensorId, rows, columns));
    }

    // Timestamped history for one sensor.
    [HttpGet("{sensorId:guid}/timestamped")]
    public ActionResult<IReadOnlyList<TelemetryHistoryReading>>
        GetTimestamped(
            Guid sensorId,
            [FromQuery] int count = 100)
    {
        if (count <= 0 || count > 10_000)
        {
            return BadRequest(
                "Count must be between 1 and 10000.");
        }

        return Ok(
            _historyService.GetTimestampedReadings(sensorId, count));
    }

    // Aggregate recent history across sensors for predictive analysis.
    [HttpGet("recent")]
    public ActionResult<IReadOnlyList<TelemetryHistoryReading>> GetRecent(
        [FromQuery] int countPerSensor = 100)
    {
        if (countPerSensor <= 0 || countPerSensor > 10_000)
        {
            return BadRequest(
                "Count per sensor must be between 1 and 10000.");
        }

        return Ok(
            _historyService.GetAllTimestampedReadings(countPerSensor));
    }

    // Sensor history counts and latest-reading timestamps.
    [HttpGet("summary")]
    public IActionResult GetSummary()
    {
        var summary = _historyService.GetHistorySummary();

        var sensors = summary.Select(entry => new
        {
            sensorId = entry.Key,
            readingCount = entry.Value,
            latestObservedAt = _historyService.GetLatestObservedAt(entry.Key)
        });

        return Ok(new
        {
            sensorCount = summary.Count,
            totalReadings = summary.Values.Sum(),
            sensors
        });
    }
}


//References
//tdykstra (2024). Create web APIs with ASP.NET Core. [online] Microsoft.com. Available at: https://learn.microsoft.com/en-us/aspnet/core/web-api/?view=aspnetcore-10.0 [Accessed 12 Sept. 2026].
//wadepickett (2025). Tutorial: Create a controller-based web API with ASP.NET Core. [online] Microsoft.com. Available at: https://learn.microsoft.com/en-us/aspnet/core/tutorials/first-web-api?view=aspnetcore-10.0&tabs=visual-studio [Accessed 12 Sept. 2026].
//tdykstra (2025). Model validation in ASP.NET Core MVC. [online] Microsoft.com. Available at: https://learn.microsoft.com/en-us/aspnet/core/mvc/models/validation?view=aspnetcore-10.0 [Accessed 12 Sept. 2026].
//tdykstra (2024). Dependency injection in ASP.NET Core. [online] Microsoft.com. Available at: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection?view=aspnetcore-10.0 [Accessed 12 Sept. 2026].