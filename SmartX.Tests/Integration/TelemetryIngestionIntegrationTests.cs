
using SmartX.Api.Controllers;
using SmartX.Api.Services;
using SmartX.Shared.DTOs;
using SmartX.Shared.Validators;
using Microsoft.AspNetCore.Mvc;

namespace SmartX.Tests.Integration;

public class TelemetryIngestionIntegrationTests
{
    [Fact]
    public void RegisteredSensor_ShouldSuccessfullyIngestTelemetryThroughApiPipeline()
    {
        // Arrange
        var sensorRegistry = new SensorRegistryService();
        var thresholdValidator = new TelemetryThresholdValidator();
        var telemetryValidator = new TelemetryValidator(thresholdValidator);
        var historyService = new TelemetryHistoryService();

        var ingestionService = new TelemetryIngestionService(
            telemetryValidator,
            historyService);

        var deploymentValidator = new RecursiveDeploymentValidator();
        var batchProcessor = new TelemetryBatchProcessor();

        var controller = new TelemetryController(
            ingestionService,
            deploymentValidator,
            sensorRegistry,
            batchProcessor);

        var sensorId = Guid.NewGuid();

        sensorRegistry.Register(
            sensorId,
            new
            {
                SensorId = sensorId,
                Name = "Integration Test Sensor",
                SensorType = "Environmental",
                Location = "Hydroponic Farm A"
            });

        var request = new TelemetryIngestionRequest
        {
            SensorDeviceId = sensorId,
            MetricName = "Soil Moisture",
            Unit = "%",
            Value = 45,
            MinimumExpectedValue = 20,
            MaximumExpectedValue = 80,
            WarningThreshold = 70,
            CriticalThreshold = 90,
            IsEnabled = true
        };

        // Act
        var result = controller.Ingest(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);

        var ingestionResult =
            Assert.IsType<TelemetryIngestionResult>(okResult.Value);

        Assert.True(ingestionResult.Success);
        Assert.False(ingestionResult.IsAnomaly);
        Assert.Equal(sensorId, ingestionResult.SensorDeviceId);
        Assert.NotEqual(Guid.Empty, ingestionResult.PacketId);
    }
}
