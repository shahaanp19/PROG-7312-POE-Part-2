
using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/telemetry-history")]
public sealed class TelemetryHistoryController : ControllerBase
{
    private const int MaximumReadingsPerRequest = 10_000;

    private readonly TelemetryHistoryService _historyService;

    public TelemetryHistoryController(
        TelemetryHistoryService historyService)
    {
        _historyService = historyService
            ?? throw new ArgumentNullException(nameof(historyService));
    }

    /// <summary>
    /// Returns numeric readings for a sensor in chronological order.
    /// </summary>
    [HttpGet("{sensorId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<double>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<double>> Get(Guid sensorId)
    {
        if (sensorId == Guid.Empty)
            return BadRequest("Sensor ID cannot be empty.");

        return Ok(_historyService.GetReadings(sensorId));
    }

    /// <summary>
    /// Returns a numeric matrix containing recent readings.
    /// </summary>
    [HttpGet("{sensorId:guid}/matrix")]
    [ProducesResponseType(typeof(double[,]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<double[,]> GetMatrix(
        Guid sensorId,
        [FromQuery] int rows = 5,
        [FromQuery] int columns = 5)
    {
        if (sensorId == Guid.Empty)
            return BadRequest("Sensor ID cannot be empty.");

        if (rows <= 0 || columns <= 0)
            return BadRequest("Rows and columns must be greater than zero.");

        // Prevent oversized matrix allocations.
        if ((long)rows * columns > MaximumReadingsPerRequest)
        {
            return BadRequest(
                $"The matrix cannot contain more than " +
                $"{MaximumReadingsPerRequest} cells.");
        }

        return Ok(_historyService.GetMatrix(sensorId, rows, columns));
    }

    /// <summary>
    /// Returns the latest timestamped readings for one sensor.
    /// </summary>
    [HttpGet("{sensorId:guid}/timestamped")]
    [ProducesResponseType(
        typeof(IReadOnlyList<TelemetryHistoryReading>),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<TelemetryHistoryReading>>
        GetTimestamped(
            Guid sensorId,
            [FromQuery] int count = 100)
    {
        if (sensorId == Guid.Empty)
            return BadRequest("Sensor ID cannot be empty.");

        if (!IsValidCount(count))
            return BadRequest(
                $"Count must be between 1 and {MaximumReadingsPerRequest}.");

        return Ok(
            _historyService.GetTimestampedReadings(sensorId, count));
    }

    /// <summary>
    /// Returns recent timestamped readings across all sensors.
    /// </summary>
    [HttpGet("recent")]
    [ProducesResponseType(
        typeof(IReadOnlyList<TelemetryHistoryReading>),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<TelemetryHistoryReading>> GetRecent(
        [FromQuery] int countPerSensor = 100)
    {
        if (!IsValidCount(countPerSensor))
        {
            return BadRequest(
                $"Count per sensor must be between 1 and " +
                $"{MaximumReadingsPerRequest}.");
        }

        return Ok(
            _historyService.GetAllTimestampedReadings(countPerSensor));
    }

    /// <summary>
    /// Returns readings inside an inclusive time range.
    /// Timestamps must be supplied in a format accepted by DateTimeOffset.
    /// </summary>
    [HttpGet("{sensorId:guid}/range")]
    [ProducesResponseType(
        typeof(IReadOnlyList<TelemetryHistoryReading>),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<TelemetryHistoryReading>> GetRange(
        Guid sensorId,
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] int? limit = 1000)
    {
        if (sensorId == Guid.Empty)
            return BadRequest("Sensor ID cannot be empty.");

        if (from > to)
            return BadRequest("'from' must not be later than 'to'.");

        if (!limit.HasValue ||
            limit.Value <= 0 ||
            limit.Value > MaximumReadingsPerRequest)
        {
            return BadRequest(
                $"Limit must be between 1 and {MaximumReadingsPerRequest}.");
        }

        var readings = _historyService.GetReadingsBetween(
            sensorId,
            from,
            to,
            limit.Value);

        return Ok(readings);
    }

    /// <summary>
    /// Returns a descriptive trend summary for a sensor.
    /// </summary>
    [HttpGet("{sensorId:guid}/trend")]
    [ProducesResponseType(
        typeof(TelemetryTrendSummary),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TelemetryTrendSummary> GetTrend(
        Guid sensorId,
        [FromQuery] int count = 20)
    {
        if (sensorId == Guid.Empty)
            return BadRequest("Sensor ID cannot be empty.");

        if (count < 2 || count > MaximumReadingsPerRequest)
        {
            return BadRequest(
                $"Count must be between 2 and " +
                $"{MaximumReadingsPerRequest}.");
        }

        var summary = _historyService.GetTrendSummary(sensorId, count);

        if (summary is null)
        {
            return NotFound(
                "Insufficient timestamped readings are available " +
                "to calculate a trend.");
        }

        return Ok(summary);
    }

    /// <summary>
    /// Returns per-sensor history counts and latest observation timestamps.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetSummary()
    {
        var summary = _historyService.GetHistorySummary();

        var sensors = summary
            .OrderBy(entry => entry.Key)
            .Select(entry => new
            {
                sensorId = entry.Key,
                readingCount = entry.Value,
                latestObservedAt =
                    _historyService.GetLatestObservedAt(entry.Key)
            })
            .ToArray();

        return Ok(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            sensorCount = sensors.Length,
            totalReadings = sensors.Sum(sensor => sensor.readingCount),
            sensors
        });
    }

    private static bool IsValidCount(int count) =>
        count > 0 && count <= MaximumReadingsPerRequest;
}



//References
//tdykstra (2024). Create web APIs with ASP.NET Core. [online] Microsoft.com. Available at: https://learn.microsoft.com/en-us/aspnet/core/web-api/?view=aspnetcore-10.0 [Accessed 12 Sept. 2026].
//wadepickett (2025). Tutorial: Create a controller-based web API with ASP.NET Core. [online] Microsoft.com. Available at: https://learn.microsoft.com/en-us/aspnet/core/tutorials/first-web-api?view=aspnetcore-10.0&tabs=visual-studio [Accessed 12 Sept. 2026].
//tdykstra (2025). Model validation in ASP.NET Core MVC. [online] Microsoft.com. Available at: https://learn.microsoft.com/en-us/aspnet/core/mvc/models/validation?view=aspnetcore-10.0 [Accessed 12 Sept. 2026].
//tdykstra (2024). Dependency injection in ASP.NET Core. [online] Microsoft.com. Available at: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection?view=aspnetcore-10.0 [Accessed 12 Sept. 2026].