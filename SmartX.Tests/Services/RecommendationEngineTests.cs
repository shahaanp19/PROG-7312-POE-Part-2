using System;
using System.Linq;
using SmartX.Web.Services;
using Xunit;

namespace SmartX.Tests.Services;

public sealed class RecommendationEngineTests
{
    private readonly CommandStateService _commandState = new();

    private RecommendationEngine CreateEngine() =>
        new(_commandState);

    [Fact]
    public void Generate_WhenLatestReadingExceedsCriticalThreshold_ReturnsCriticalRecommendation()
    {
        var now = DateTimeOffset.UtcNow;
        var telemetry = new[]
        {
            Reading(now.AddMinutes(-1), 85, warning: 70, critical: 80)
        };

        var recommendations = CreateEngine().Generate(telemetry);

        Assert.Contains(
            recommendations,
            r => r.Severity == RecommendationSeverity.Critical &&
                 r.Title.Contains("Critical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_WhenTrendProjectsWarningThresholdCrossing_ReturnsPrediction()
    {
        var now = DateTimeOffset.UtcNow;
        var telemetry = new[]
        {
            Reading(now.AddMinutes(-5), 40, warning: 90, critical: 250),
            Reading(now.AddMinutes(-3), 50, warning: 90, critical: 250),
            Reading(now.AddMinutes(-1), 60, warning: 90, critical: 250)
        };

        var recommendations = CreateEngine().Generate(telemetry);

        Assert.Contains(
            recommendations,
            r => r.Title.StartsWith(
                "Predicted high",
                StringComparison.OrdinalIgnoreCase) &&
                 r.Severity == RecommendationSeverity.Warning);
    }

    [Fact]
    public void Generate_WhenLatestReadingIsStale_ReturnsStaleTelemetryRecommendation()
    {
        var telemetry = new[]
        {
            Reading(
                DateTimeOffset.UtcNow.AddMinutes(-20),
                40,
                warning: 90,
                critical: 150)
        };

        var recommendations = CreateEngine().Generate(telemetry);

        Assert.Contains(
            recommendations,
            r => r.Title.StartsWith(
                "Stale",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_WhenOldReadingIsFollowedByFreshReading_DoesNotReportStaleTelemetry()
    {
        var now = DateTimeOffset.UtcNow;
        var telemetry = new[]
        {
            Reading(now.AddMinutes(-30), 50, warning: 90, critical: 150),
            Reading(now.AddMinutes(-2), 50, warning: 90, critical: 150)
        };

        var recommendations = CreateEngine().Generate(telemetry);

        Assert.DoesNotContain(
            recommendations,
            r => r.Title.StartsWith(
                "Stale",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_WhenCommandQueueHasTenPendingCommands_ReturnsBacklogRecommendation()
    {
        for (var i = 0; i < 10; i++)
        {
            _commandState.Enqueue(
                $"device-{i}",
                "ReadStatus",
                "Normal");
        }

        var recommendations = CreateEngine().Generate();

        Assert.Contains(
            recommendations,
            r => r.Title.Contains(
                "Command queue backlog",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_WhenTelemetryIsEmpty_ReturnsOperationalRecommendationsWithoutThrowing()
    {
        var recommendations = CreateEngine().Generate(
            Enumerable.Empty<TelemetryObservation>());

        Assert.NotNull(recommendations);
    }

    private static TelemetryObservation Reading(
        DateTimeOffset observedAt,
        double value,
        double warning,
        double critical)
    {
        return new TelemetryObservation
        {
            DeviceId = Guid.Parse(
                "11111111-1111-1111-1111-111111111111"),
            Metric = "Temperature",
            Value = value,
            Unit = "°C",
            ObservedAt = observedAt,
            WarningThreshold = warning,
            CriticalThreshold = critical
        };
    }
}
