
using System.Collections.Concurrent;
using SmartX.Api.Services;

namespace SmartX.Tests.Services;

public class TelemetryHistoryServiceTests
{
    [Theory]
    [InlineData(30, 20, 10, 1L, 0L, 0L, 0L)] // LL
    [InlineData(10, 20, 30, 0L, 1L, 0L, 0L)] // RR
    [InlineData(30, 10, 20, 0L, 0L, 1L, 0L)] // LR
    [InlineData(10, 30, 20, 0L, 0L, 0L, 1L)] // RL
    public void AddReading_RotationPatterns_PreserveSortedHistoryAndBalance(
        int first,
        int second,
        int third,
        long expectedLl,
        long expectedRr,
        long expectedLr,
        long expectedRl)
    {
        var service = new TelemetryHistoryService();
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        service.AddReading(sensorId, first, start.AddMinutes(first));
        service.AddReading(sensorId, second, start.AddMinutes(second));
        service.AddReading(sensorId, third, start.AddMinutes(third));

        var readings = service.GetTimestampedReadings(sensorId);
        var diagnostics = service.GetTreeDiagnostics(sensorId);

        Assert.Equal(3, readings.Count);
        Assert.Equal(
            new[] { 10.0, 20.0, 30.0 },
            readings.Select(reading => reading.Value).ToArray());

        Assert.Equal(3, diagnostics.NodeCount);
        Assert.Equal(2, diagnostics.Height);
        Assert.True(diagnostics.IsBalanced);

        Assert.Equal(expectedLl, diagnostics.LeftLeftRotations);
        Assert.Equal(expectedRr, diagnostics.RightRightRotations);
        Assert.Equal(expectedLr, diagnostics.LeftRightRotations);
        Assert.Equal(expectedRl, diagnostics.RightLeftRotations);
        Assert.Equal(1, diagnostics.TotalRotations);
    }

    [Fact]
    public void AddReading_LargeAscendingSequence_PreservesChronologicalOrderAndBalance()
    {
        const int readingCount = 10_000;

        var service = new TelemetryHistoryService(20_000);
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < readingCount; i++)
            service.AddReading(sensorId, i, start.AddTicks(i));

        var readings = service.GetTimestampedReadings(sensorId);
        var diagnostics = service.GetTreeDiagnostics(sensorId);

        Assert.Equal(readingCount, readings.Count);
        Assert.Equal(0, readings[0].Value);
        Assert.Equal(9_999, readings[^1].Value);
        AssertChronological(readings);

        Assert.Equal(readingCount, diagnostics.NodeCount);
        Assert.True(diagnostics.IsBalanced);

        // A balanced AVL tree must have logarithmic height.
        var maximumExpectedHeight =
            2 * Math.Ceiling(Math.Log2(readingCount + 1));

        Assert.True(
            diagnostics.Height <= maximumExpectedHeight,
            $"AVL height {diagnostics.Height} exceeded the expected bound " +
            $"{maximumExpectedHeight} for {readingCount} readings.");

        Assert.True(diagnostics.TotalRotations > 0);
    }

    [Fact]
    public void AddReading_DuplicateTimestamps_RetainsEveryReadingInInsertionOrder()
    {
        var service = new TelemetryHistoryService();
        var sensorId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;

        service.AddReading(sensorId, 10, timestamp);
        service.AddReading(sensorId, 20, timestamp);
        service.AddReading(sensorId, 30, timestamp);

        var readings = service.GetTimestampedReadings(sensorId);

        Assert.Equal(3, readings.Count);
        Assert.Equal(
            new[] { 10.0, 20.0, 30.0 },
            readings.Select(reading => reading.Value).ToArray());

        Assert.All(
            readings,
            reading => Assert.Equal(timestamp, reading.ObservedAt));

        var diagnostics = service.GetTreeDiagnostics(sensorId);

        Assert.Equal(3, diagnostics.NodeCount);
        Assert.True(diagnostics.IsBalanced);
    }

    [Fact]
    public void GetReadingsBetween_IncludesBothBoundaryTimestamps()
    {
        var service = new TelemetryHistoryService();
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        service.AddReading(sensorId, 1, start);
        service.AddReading(sensorId, 2, start.AddMinutes(1));
        service.AddReading(sensorId, 3, start.AddMinutes(2));
        service.AddReading(sensorId, 4, start.AddMinutes(3));
        service.AddReading(sensorId, 5, start.AddMinutes(4));

        var result = service.GetReadingsBetween(
            sensorId,
            start.AddMinutes(1),
            start.AddMinutes(3));

        Assert.Equal(
            new[] { 2.0, 3.0, 4.0 },
            result.Select(reading => reading.Value).ToArray());
    }

    [Fact]
    public void GetReadingsBetween_IncludesAllDuplicateBoundaryTimestamps()
    {
        var service = new TelemetryHistoryService();
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        service.AddReading(sensorId, 1, start);
        service.AddReading(sensorId, 2, start.AddMinutes(1));
        service.AddReading(sensorId, 3, start.AddMinutes(1));
        service.AddReading(sensorId, 4, start.AddMinutes(2));

        var result = service.GetReadingsBetween(
            sensorId,
            start.AddMinutes(1),
            start.AddMinutes(1));

        Assert.Equal(
            new[] { 2.0, 3.0 },
            result.Select(reading => reading.Value).ToArray());
    }

    [Fact]
    public void GetReadingsBetween_AppliesResultLimit()
    {
        var service = new TelemetryHistoryService();
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
            service.AddReading(sensorId, i, start.AddMinutes(i));

        var result = service.GetReadingsBetween(
            sensorId,
            start,
            start.AddMinutes(9),
            limit: 3);

        Assert.Equal(3, result.Count);
        Assert.Equal(
            new[] { 0.0, 1.0, 2.0 },
            result.Select(reading => reading.Value).ToArray());
    }

    [Fact]
    public void GetReadingsBetween_RejectsReversedRange()
    {
        var service = new TelemetryHistoryService();
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() =>
            service.GetReadingsBetween(
                sensorId,
                start.AddMinutes(1),
                start));
    }

    [Fact]
    public void GetReadingsBetween_RejectsZeroLimit()
    {
        var service = new TelemetryHistoryService();
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.GetReadingsBetween(
                sensorId,
                start,
                start.AddMinutes(1),
                limit: 0));
    }

    [Fact]
    public void AddReading_EnforcesPerSensorCapacity()
    {
        const int capacity = 5;

        var service = new TelemetryHistoryService(capacity);
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
            service.AddReading(sensorId, i, start.AddMinutes(i));

        var readings = service.GetTimestampedReadings(sensorId);
        var diagnostics = service.GetTreeDiagnostics(sensorId);

        Assert.Equal(capacity, readings.Count);
        Assert.Equal(
            new[] { 5.0, 6.0, 7.0, 8.0, 9.0 },
            readings.Select(reading => reading.Value).ToArray());

        Assert.Equal(capacity, service.GetReadingCount(sensorId));
        Assert.Equal(capacity, diagnostics.NodeCount);
        Assert.True(diagnostics.IsBalanced);
    }

    [Fact]
    public void AddReading_CapacityEvictsOldestTimestampNotOldestInsertion()
    {
        var service = new TelemetryHistoryService(3);
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        service.AddReading(sensorId, 3, start.AddMinutes(3));
        service.AddReading(sensorId, 1, start.AddMinutes(1));
        service.AddReading(sensorId, 2, start.AddMinutes(2));
        service.AddReading(sensorId, 4, start.AddMinutes(4));

        var readings = service.GetTimestampedReadings(sensorId);
        var diagnostics = service.GetTreeDiagnostics(sensorId);

        Assert.Equal(
            new[] { 2.0, 3.0, 4.0 },
            readings.Select(reading => reading.Value).ToArray());

        Assert.Equal(3, diagnostics.NodeCount);
        Assert.True(diagnostics.IsBalanced);
    }

    [Fact]
    public void GetTreeDiagnostics_UnknownSensor_ReturnsEmptyBalancedTree()
    {
        var service = new TelemetryHistoryService();

        var diagnostics = service.GetTreeDiagnostics(Guid.NewGuid());

        Assert.Equal(0, diagnostics.Height);
        Assert.Equal(0, diagnostics.NodeCount);
        Assert.True(diagnostics.IsBalanced);
        Assert.Equal(0, diagnostics.TotalRotations);
    }

    [Fact]
    public void RemoveHistory_RemovesSensorAndItsDiagnostics()
    {
        var service = new TelemetryHistoryService();
        var sensorId = Guid.NewGuid();

        service.AddReading(sensorId, 10, DateTimeOffset.UtcNow);

        Assert.True(service.RemoveHistory(sensorId));
        Assert.Equal(0, service.GetReadingCount(sensorId));

        var diagnostics = service.GetTreeDiagnostics(sensorId);

        Assert.Equal(0, diagnostics.NodeCount);
        Assert.Equal(0, diagnostics.Height);
        Assert.True(diagnostics.IsBalanced);
    }

    [Fact]
    public async Task ConcurrentInsertsAndRangeQueries_RemainConsistent()
    {
        const int writerCount = 8;
        const int readingsPerWriter = 1_000;
        const int expectedCount = writerCount * readingsPerWriter;

        var service = new TelemetryHistoryService(expectedCount + 100);
        var sensorId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        var errors = new ConcurrentQueue<Exception>();

        var writers = Enumerable.Range(0, writerCount)
            .Select(writer => Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < readingsPerWriter; i++)
                    {
                        var sequence =
                            writer * readingsPerWriter + i;

                        service.AddReading(
                            sensorId,
                            sequence,
                            start.AddTicks(sequence));
                    }
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception);
                }
            }));

        var readers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < 500; i++)
                    {
                        var snapshot =
                            service.GetTimestampedReadings(sensorId);

                        AssertChronological(snapshot);

                        var range = service.GetReadingsBetween(
                            sensorId,
                            start,
                            start.AddTicks(expectedCount));

                        AssertChronological(range);
                    }
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception);
                }
            }));

        await Task.WhenAll(writers.Concat(readers));

        Assert.Empty(errors);

        var finalSnapshot =
            service.GetTimestampedReadings(sensorId);

        var diagnostics = service.GetTreeDiagnostics(sensorId);

        Assert.Equal(expectedCount, finalSnapshot.Count);
        AssertChronological(finalSnapshot);
        Assert.Equal(expectedCount, service.GetReadingCount(sensorId));

        Assert.Equal(expectedCount, diagnostics.NodeCount);
        Assert.True(diagnostics.IsBalanced);
    }

    [Fact]
    public async Task ConcurrentSensors_MaintainIndependentHistories()
    {
        const int sensorCount = 10;
        const int readingsPerSensor = 500;

        var service = new TelemetryHistoryService(
            readingsPerSensor + 10);

        var sensorIds = Enumerable.Range(0, sensorCount)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        var tasks = sensorIds.Select(sensorId => Task.Run(() =>
        {
            for (var i = 0; i < readingsPerSensor; i++)
            {
                service.AddReading(
                    sensorId,
                    i,
                    DateTimeOffset.UtcNow.AddTicks(i));
            }
        }));

        await Task.WhenAll(tasks);

        foreach (var sensorId in sensorIds)
        {
            Assert.Equal(
                readingsPerSensor,
                service.GetReadingCount(sensorId));

            Assert.Equal(
                readingsPerSensor,
                service.GetTimestampedReadings(sensorId).Count);

            var diagnostics =
                service.GetTreeDiagnostics(sensorId);

            Assert.Equal(readingsPerSensor, diagnostics.NodeCount);
            Assert.True(diagnostics.IsBalanced);
        }

        Assert.Equal(sensorCount, service.GetHistorySummary().Count);
    }

    [Fact]
    public void GetTimestampedReadings_ReturnsIndependentSnapshot()
    {
        var service = new TelemetryHistoryService();
        var sensorId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;

        service.AddReading(sensorId, 1, timestamp);

        var firstSnapshot =
            service.GetTimestampedReadings(sensorId);

        service.AddReading(
            sensorId,
            2,
            timestamp.AddMinutes(1));

        var secondSnapshot =
            service.GetTimestampedReadings(sensorId);

        Assert.Single(firstSnapshot);
        Assert.Equal(2, secondSnapshot.Count);
    }

    private static void AssertChronological(
        IReadOnlyList<TelemetryHistoryReading> readings)
    {
        for (var i = 1; i < readings.Count; i++)
        {
            Assert.True(
                readings[i - 1].ObservedAt <=
                readings[i].ObservedAt,
                "History readings must be ordered by observed timestamp.");
        }
    }
}
