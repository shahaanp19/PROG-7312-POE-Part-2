
using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Controllers;
using SmartX.Api.Services;
using SmartX.Shared.DTOs;
using SmartX.Shared.Validators;

namespace SmartX.Tests.Api;

public class TelemetryControllerTests
{
    private readonly TelemetryController _controller;
    private readonly SensorRegistryService _sensorRegistry;

    public TelemetryControllerTests()
    {
        var thresholdValidator = new TelemetryThresholdValidator();
        var telemetryValidator = new TelemetryValidator(thresholdValidator);
        var historyService = new TelemetryHistoryService();

        var ingestionService = new TelemetryIngestionService(
            telemetryValidator,
            historyService);

        var deploymentValidator = new RecursiveDeploymentValidator();
        _sensorRegistry = new SensorRegistryService();
        var batchProcessor = new TelemetryBatchProcessor();

        _controller = new TelemetryController(
            ingestionService,
            deploymentValidator,
            _sensorRegistry,
            batchProcessor);
    }

    [Fact]
    public void Health_ShouldReturnHealthyStatus()
    {
        var result = _controller.Health();

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public void Ingest_ShouldProcessValidTelemetry()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);

        var result = _controller.Ingest(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var ingestionResult = Assert.IsType<TelemetryIngestionResult>(okResult.Value);

        Assert.True(ingestionResult.Success);
        Assert.Equal(request.SensorDeviceId, ingestionResult.SensorDeviceId);
    }

    [Fact]
    public void Ingest_ShouldRejectEmptySensorIdentifier()
    {
        var request = CreateValidRequest();
        request.SensorDeviceId = Guid.Empty;

        var result = _controller.Ingest(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public void Ingest_ShouldReturnNotFoundForUnregisteredSensor()
    {
        var request = CreateValidRequest();

        var result = _controller.Ingest(request);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.NotNull(notFound.Value);
    }

    [Fact]
    public void Ingest_ShouldRejectMissingMetricName()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);
        request.MetricName = string.Empty;

        var result = _controller.Ingest(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public void Ingest_ShouldRejectMissingUnit()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);
        request.Unit = string.Empty;

        var result = _controller.Ingest(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public void Ingest_ShouldRejectNaNTelemetry()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);
        request.Value = double.NaN;

        var result = _controller.Ingest(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public void Ingest_ShouldRejectPositiveInfinity()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);
        request.Value = double.PositiveInfinity;

        var result = _controller.Ingest(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public void Ingest_ShouldRejectNegativeInfinity()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);
        request.Value = double.NegativeInfinity;

        var result = _controller.Ingest(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public void Ingest_ShouldRejectInvalidExpectedValueRange()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);
        request.MinimumExpectedValue = 100;
        request.MaximumExpectedValue = 50;

        var result = _controller.Ingest(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public void Ingest_ShouldRejectInvalidWarningCriticalThresholds()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);
        request.WarningThreshold = 90;
        request.CriticalThreshold = 70;

        var result = _controller.Ingest(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public void Ingest_ShouldReturnUnprocessableEntityWhenMetricIsDisabled()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);
        request.IsEnabled = false;

        var result = _controller.Ingest(request);

        var response = Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
        var ingestionResult = Assert.IsType<TelemetryIngestionResult>(response.Value);

        Assert.False(ingestionResult.Success);
    }

    [Fact]
    public void Ingest_ShouldDetectTelemetryAnomaly()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);

        request.Value = 60;
        request.MinimumExpectedValue = 0;
        request.MaximumExpectedValue = 50;
        request.WarningThreshold = 70;
        request.CriticalThreshold = 90;

        var result = _controller.Ingest(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var ingestionResult = Assert.IsType<TelemetryIngestionResult>(okResult.Value);

        Assert.True(ingestionResult.Success);
        Assert.True(ingestionResult.IsAnomaly);
    }

    [Fact]
    public void Ingest_ShouldDetectWarningThreshold()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);

        request.Value = 45;
        request.WarningThreshold = 40;
        request.CriticalThreshold = 50;

        var result = _controller.Ingest(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var ingestionResult = Assert.IsType<TelemetryIngestionResult>(okResult.Value);

        Assert.True(ingestionResult.Success);
        Assert.True(ingestionResult.IsAnomaly);
    }

    [Fact]
    public void Ingest_ShouldDetectCriticalThreshold()
    {
        var request = CreateValidRequest();
        RegisterSensor(request.SensorDeviceId);

        request.Value = 95;
        request.WarningThreshold = 70;
        request.CriticalThreshold = 90;

        var result = _controller.Ingest(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var ingestionResult = Assert.IsType<TelemetryIngestionResult>(okResult.Value);

        Assert.True(ingestionResult.Success);
        Assert.True(ingestionResult.IsAnomaly);
    }

    [Fact]
    public void GenericDemo_ShouldReturnSuccessfulResponse()
    {
        var result = _controller.GenericDemo();

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public void RecursiveValidationDemo_ShouldReturnSuccessfulResponse()
    {
        var result = _controller.RecursiveValidationDemo();

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public void RecursiveValidationFailureDemo_ShouldReturnSuccessfulResponse()
    {
        var result = _controller.RecursiveValidationFailureDemo();

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public void RecursiveValidationMissingDemo_ShouldReturnSuccessfulResponse()
    {
        var result = _controller.RecursiveValidationMissingDemo();

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public void HistoricalBatchDemo_ShouldReturnSuccessfulResponse()
    {
        var result = _controller.HistoricalBatchDemo();

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    private void RegisterSensor(Guid sensorId)
    {
        var registered = _sensorRegistry.Register(
            sensorId,
            new
            {
                SensorId = sensorId,
                Name = "Test Sensor",
                SensorType = "Environmental"
            });

        Assert.True(registered);
    }

    private static TelemetryIngestionRequest CreateValidRequest()
    {
        return new TelemetryIngestionRequest
        {
            SensorDeviceId = Guid.NewGuid(),
            MetricName = "Temperature",
            Unit = "°C",
            Value = 25,
            MinimumExpectedValue = 0,
            MaximumExpectedValue = 50,
            WarningThreshold = 40,
            CriticalThreshold = 45,
            IsEnabled = true
        };
    }
}
