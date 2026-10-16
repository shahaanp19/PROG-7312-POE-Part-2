
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SmartX.Web.Services;

public sealed record DiagnosticResult(
    string Name,
    bool Passed,
    string Details,
    long ElapsedMilliseconds);

public static class CommandCollectionDiagnostics
{
    public static IReadOnlyList<DiagnosticResult> RunAll()
    {
        return new List<DiagnosticResult>
        {
            Run("Priority ordering", TestPriorityOrdering),
            Run("FIFO within equal priority", TestEqualPriorityFifo),
            Run("Priority-aware ProcessNext", TestProcessNextPriority),
            Run("Undo newest pending command", TestUndo),
            Run("Undo skips processed commands", TestUndoSkipsProcessed),
            Run("Critical alert ingestion", TestCriticalAlertIngestion),
            Run("Queue clearing preserves history", TestClearQueue),

            Run("Device dictionary lookup", TestDeviceIndex),
            Run("Device cache updates after insertion", TestDeviceCacheUpdates),
            Run("Sorted timeline ordering", TestTimelineOrdering),
            Run("Timeline date-range filtering", TestTimelineDateRange),
            Run("Timeline device filtering", TestTimelineDeviceFilter),
            Run("Timeline result limit", TestTimelineLimit),
            Run("Audit trail ordering", TestAuditTrail),
            Run("Audit log clearing", TestClearAuditLog),

            // Unique Collections: Sets
            Run("HashSet unique command tracking", TestUniqueCommandTracking),
            Run("HashSet pending membership lifecycle", TestPendingSetLifecycle),
            Run("Duplicate active-error prevention", TestDuplicateActiveErrors),
            Run("HashSet consistency after queue clearing", TestSetConsistencyAfterClear),
            Run("HashSet consistency under repeated operations", TestRepeatedSetOperations),
            Run("HashSet consistency under concurrent submissions", TestConcurrentSetConsistency),

            Run("Repeated collection operations", TestRepeatedOperations),
            Run("Concurrent command submissions", TestConcurrentSubmissions),
            Run("Concurrent critical alert ingestion", TestConcurrentAlerts),
            Run("Indexed lookup and timeline workload", TestLookupWorkload)
        };
    }

    private static DiagnosticResult Run(string name, Action test)
    {
        var timer = Stopwatch.StartNew();

        try
        {
            test();
            timer.Stop();

            return new DiagnosticResult(
                name,
                true,
                "Passed",
                timer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            timer.Stop();

            return new DiagnosticResult(
                name,
                false,
                ex.Message,
                timer.ElapsedMilliseconds);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static CommandStateService CreateService() => new();

    private static void TestPriorityOrdering()
    {
        var service = CreateService();

        service.Enqueue("D1", "Normal", "Normal");
        service.Enqueue("D2", "High", "High");
        service.EnqueueCriticalAlert("D3", "Critical");

        Assert(service.ProcessNext()?.Priority == "Critical",
            "Critical command was not processed first.");

        Assert(service.ProcessNext()?.Priority == "High",
            "High-priority command was not processed second.");

        Assert(service.ProcessNext()?.Priority == "Normal",
            "Normal command was not processed last.");

        Assert(service.QueueCount == 0,
            "Queue should be empty.");
    }

    private static void TestEqualPriorityFifo()
    {
        var service = CreateService();

        service.Enqueue("D1", "First", "High");
        service.Enqueue("D2", "Second", "High");
        service.Enqueue("D3", "Third", "High");

        Assert(service.ProcessNext()?.Command == "First",
            "First equal-priority command was not processed first.");

        Assert(service.ProcessNext()?.Command == "Second",
            "Second equal-priority command was not processed second.");

        Assert(service.ProcessNext()?.Command == "Third",
            "Third equal-priority command was not processed third.");
    }

    private static void TestProcessNextPriority()
    {
        var service = CreateService();

        service.Enqueue("D1", "Ordinary", "Normal");
        service.EnqueueCriticalAlert("D2", "Emergency");

        Assert(service.ProcessNext()?.Command == "Emergency",
            "ProcessNext did not select the critical alert.");
    }

    private static void TestUndo()
    {
        var service = CreateService();

        service.Enqueue("D1", "First", "Normal");
        service.Enqueue("D2", "Second", "Normal");
        service.Enqueue("D3", "Third", "Normal");

        Assert(service.UndoLast()?.Command == "Third",
            "Undo did not remove the newest command.");

        Assert(service.QueueCount == 2,
            "Undo left an incorrect pending count.");

        Assert(service.ProcessNext()?.Command == "First",
            "Undo corrupted FIFO order.");

        Assert(service.ProcessNext()?.Command == "Second",
            "The remaining command was not preserved.");
    }

    private static void TestUndoSkipsProcessed()
    {
        var service = CreateService();

        service.Enqueue("D1", "First", "Normal");
        service.Enqueue("D2", "Second", "Normal");

        Assert(service.ProcessNext()?.Command == "First",
            "Expected first command to be processed.");

        Assert(service.UndoLast()?.Command == "Second",
            "Undo should skip the already processed command.");

        Assert(service.UndoLast() is null,
            "Undo should return null when nothing is pending.");
    }

    private static void TestCriticalAlertIngestion()
    {
        var service = CreateService();

        var record = service.EnqueueCriticalAlert(
            "Sensor-001",
            "EmergencyStop",
            "temperature=95");

        Assert(record.Priority == "Critical",
            "Alert was not assigned Critical priority.");

        Assert(record.Status == "Queued",
            "Alert should be queued.");

        Assert(service.CriticalQueueCount == 1,
            "Critical queue count is incorrect.");

        Assert(service.GetCommandsForDevice("Sensor-001").Count == 1,
            "Alert was not indexed by device.");

        Assert(service.ProcessNext()?.Id == record.Id,
            "Critical alert was not selected for processing.");
    }

    private static void TestClearQueue()
    {
        var service = CreateService();

        service.Enqueue("D1", "One", "Normal");
        service.Enqueue("D2", "Two", "High");
        service.EnqueueCriticalAlert("D3", "Three");

        var cleared = service.ClearQueue();

        Assert(cleared == 3,
            "ClearQueue returned an incorrect count.");

        Assert(service.QueueCount == 0,
            "Queue was not cleared.");

        Assert(service.HistoryCount == 3,
            "History was not preserved.");

        Assert(service.History.All(record => record.Status == "Cleared"),
            "Cleared records have inconsistent statuses.");

        Assert(service.ProcessNext() is null,
            "A command remained processable after clearing.");
    }

    private static void TestDeviceIndex()
    {
        var service = CreateService();

        service.Enqueue("Sensor-001", "Read", "Normal");
        service.Enqueue("Sensor-002", "Reset", "High");
        service.Enqueue("Sensor-001", "Calibrate", "High");

        var records = service.GetCommandsForDevice("sensor-001");

        Assert(records.Count == 2,
            "Device dictionary returned an incorrect number of commands.");

        Assert(records.All(record =>
                record.DeviceId.Equals(
                    "Sensor-001",
                    StringComparison.OrdinalIgnoreCase)),
            "Device dictionary returned a command for another device.");

        Assert(service.GetCommandsForDevice("UNKNOWN").Count == 0,
            "Unknown device should return an empty result.");

        Assert(service.GetCommandsForDevice(null).Count == 0,
            "Null device ID should return an empty result.");
    }

    private static void TestDeviceCacheUpdates()
    {
        var service = CreateService();

        service.Enqueue("Sensor-001", "First", "Normal");

        var firstLookup = service.GetCommandsForDevice("sensor-001");

        Assert(firstLookup.Count == 1,
            "Initial device lookup returned the wrong count.");

        service.Enqueue("SENSOR-001", "Second", "High");

        var secondLookup = service.GetCommandsForDevice("Sensor-001");

        Assert(secondLookup.Count == 2,
            "Device cache did not reflect a subsequent insertion.");

        Assert(secondLookup[0].Command == "First",
            "Device commands lost insertion order.");

        Assert(secondLookup[1].Command == "Second",
            "The latest device command was not cached.");

        Assert(service.GetCommandsForDevice("Sensor-002").Count == 0,
            "A different device received incorrect cached results.");
    }

    private static void TestTimelineOrdering()
    {
        var service = CreateService();

        for (var i = 0; i < 30; i++)
            service.Enqueue($"Device-{i % 3}", $"Command-{i}");

        var timeline = service.GetCommandTimeline();

        Assert(timeline.Count == 30,
            "Timeline returned an incorrect number of commands.");

        Assert(timeline.SequenceEqual(
                timeline.OrderByDescending(record => record.CreatedAt)
                    .ThenByDescending(record => record.Id)),
            "Timeline is not sorted newest first.");

        Assert(service.History.SequenceEqual(timeline),
            "History and timeline ordering differ.");
    }

    private static void TestTimelineDateRange()
    {
        var service = CreateService();

        var first = service.Enqueue("D1", "First");
        var middle = service.Enqueue("D1", "Middle");
        var last = service.Enqueue("D1", "Last");

        var range = service.GetCommandTimeline(
            from: middle.CreatedAt,
            to: middle.CreatedAt);

        Assert(range.Any(record => record.Id == middle.Id),
            "Timeline omitted a command within the requested date range.");

        Assert(range.All(record =>
                record.CreatedAt == middle.CreatedAt),
            "Timeline returned commands outside the requested date range.");

        var inclusiveRange = service.GetCommandTimeline(
            from: first.CreatedAt,
            to: last.CreatedAt);

        Assert(inclusiveRange.Count == 3,
            "Inclusive timeline range did not return all matching commands.");

        var rejectedInvalidRange = false;

        try
        {
            service.GetCommandTimeline(
                from: last.CreatedAt,
                to: first.CreatedAt);
        }
        catch (ArgumentException)
        {
            rejectedInvalidRange = true;
        }

        Assert(rejectedInvalidRange,
            "Timeline accepted a start time later than its end time.");
    }

    private static void TestTimelineDeviceFilter()
    {
        var service = CreateService();

        service.Enqueue("Sensor-A", "Read");
        service.Enqueue("Sensor-B", "Reset");
        service.Enqueue("Sensor-A", "Calibrate");
        service.Enqueue("Sensor-C", "Inspect");

        var timeline = service.GetCommandTimeline(deviceId: "sensor-a");

        Assert(timeline.Count == 2,
            "Device-filtered timeline returned an incorrect count.");

        Assert(timeline.All(record =>
                record.DeviceId.Equals(
                    "Sensor-A",
                    StringComparison.OrdinalIgnoreCase)),
            "Device-filtered timeline contains another device.");

        Assert(service.GetCommandTimeline(deviceId: "Unknown").Count == 0,
            "Unknown device should return an empty timeline.");
    }

    private static void TestTimelineLimit()
    {
        var service = CreateService();

        for (var i = 0; i < 20; i++)
            service.Enqueue("D1", $"Command-{i}");

        var limited = service.GetCommandTimeline(limit: 5);

        Assert(limited.Count == 5,
            "Timeline did not respect its result limit.");

        Assert(limited[0].Command == "Command-19",
            "Limited timeline did not return the newest commands first.");

        Assert(limited[^1].Command == "Command-15",
            "Limited timeline returned an incorrect final command.");

        var rejectedInvalidLimit = false;

        try
        {
            service.GetCommandTimeline(limit: 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejectedInvalidLimit = true;
        }

        Assert(rejectedInvalidLimit,
            "Timeline accepted a zero result limit.");
    }

    private static void TestAuditTrail()
    {
        var service = CreateService();
        var record = service.Enqueue("D1", "Inspect", "Normal");

        service.ProcessNext();

        var entries = service.AuditLog
            .Where(entry => entry.CommandId == record.Id)
            .ToArray();

        Assert(entries.Length >= 2,
            "Expected queue and processing audit entries.");

        Assert(entries[0].Sequence > entries[^1].Sequence,
            "Audit trail is not ordered newest first.");
    }

    private static void TestClearAuditLog()
    {
        var service = CreateService();

        service.Enqueue("D1", "Inspect");
        service.RecordActiveError("sensor-overheat");

        Assert(service.AuditLog.Count > 0,
            "Expected audit entries before clearing.");

        service.ClearAuditLog();

        Assert(service.AuditLog.Count == 0,
            "Audit timeline was not cleared.");

        Assert(service.HistoryCount == 1,
            "Clearing the audit log unexpectedly removed command history.");

        Assert(service.ActiveErrorCount == 1,
            "Clearing the audit log unexpectedly removed active errors.");
    }

    private static void TestUniqueCommandTracking()
    {
        var service = CreateService();

        var first = service.Enqueue("D1", "First");
        var second = service.Enqueue("D1", "Second");
        var third = service.Enqueue("D2", "Third");

        Assert(service.UniqueCommandCount == 3,
            "Unique command tracking returned an incorrect count.");

        Assert(service.ContainsCommand(first.Id) &&
               service.ContainsCommand(second.Id) &&
               service.ContainsCommand(third.Id),
            "One or more command IDs were not tracked.");

        Assert(!service.ContainsCommand(Guid.Empty),
            "An unknown command ID was reported as tracked.");

        Assert(service.Queue.Select(record => record.Id).Distinct().Count()
                == service.QueueCount,
            "Pending queue contains duplicate command IDs.");

        Assert(service.History.Select(record => record.Id).Distinct().Count()
                == service.HistoryCount,
            "Command history contains duplicate command IDs.");

        Assert(service.Queue.All(record => service.ContainsCommand(record.Id)),
            "A queued command is missing from unique command tracking.");
    }

    private static void TestPendingSetLifecycle()
    {
        var service = CreateService();

        var first = service.Enqueue("D1", "First");
        var second = service.Enqueue("D1", "Second");

        Assert(service.IsCommandPending(first.Id) &&
               service.IsCommandPending(second.Id),
            "Newly enqueued commands were not marked pending.");

        var processed = service.ProcessNext();

        Assert(processed?.Id == first.Id,
            "The first command was not processed.");

        Assert(!service.IsCommandPending(first.Id),
            "Processed command remained in the pending set.");

        Assert(service.IsCommandPending(second.Id),
            "Processing one command incorrectly removed another pending ID.");

        var undone = service.UndoLast();

        Assert(undone?.Id == second.Id,
            "Undo did not remove the expected pending command.");

        Assert(!service.IsCommandPending(second.Id),
            "Undone command remained in the pending set.");

        Assert(service.QueueCount == 0,
            "Pending queue should be empty after processing and undo.");

        Assert(service.UniqueCommandCount == 2,
            "Processing or undo incorrectly changed unique command tracking.");

        Assert(service.ContainsCommand(first.Id) &&
               service.ContainsCommand(second.Id),
            "Processing or undo removed historical command tracking.");
    }

    private static void TestDuplicateActiveErrors()
    {
        var service = CreateService();

        service.RecordActiveError("sensor-overheat");
        service.RecordActiveError("sensor-overheat");
        service.RecordActiveError("SENSOR-OVERHEAT");
        service.RecordActiveError("Sensor-Overheat");

        Assert(service.ActiveErrorCount == 1,
            "Case variations of one error were counted as distinct errors.");

        service.RecordActiveError("sensor-offline");
        service.RecordActiveError("SENSOR-OFFLINE");

        Assert(service.ActiveErrorCount == 2,
            "Distinct active errors were not tracked uniquely.");

        service.ResolveActiveError("SENSOR-OVERHEAT");

        Assert(service.ActiveErrorCount == 1,
            "Resolving an error did not remove its case-insensitive key.");

        service.ResolveActiveError("sensor-offline");

        Assert(service.ActiveErrorCount == 0,
            "Resolved errors remain in the active-error set.");
    }

    private static void TestSetConsistencyAfterClear()
    {
        var service = CreateService();

        var first = service.Enqueue("D1", "First");
        var second = service.Enqueue("D2", "Second");
        var third = service.EnqueueCriticalAlert("D3", "Third");

        var ids = new[] { first.Id, second.Id, third.Id };

        Assert(service.QueueCount == ids.Length,
            "Setup did not create three pending commands.");

        Assert(service.UniqueCommandCount == ids.Length,
            "Setup did not create three unique command IDs.");

        Assert(ids.All(service.IsCommandPending),
            "A command was not pending before queue clearing.");

        var cleared = service.ClearQueue();

        Assert(cleared == ids.Length,
            "ClearQueue returned an incorrect number of cleared commands.");

        Assert(service.QueueCount == 0,
            "Pending queue is not empty after clearing.");

        Assert(ids.All(id => !service.IsCommandPending(id)),
            "Cleared command IDs remain in the pending set.");

        Assert(service.UniqueCommandCount == ids.Length,
            "Clearing the queue unexpectedly removed unique command tracking.");

        Assert(ids.All(service.ContainsCommand),
            "Cleared commands were removed from command history tracking.");

        Assert(service.HistoryCount == ids.Length,
            "Clearing the queue unexpectedly removed command history.");
    }

    private static void TestRepeatedSetOperations()
    {
        var service = CreateService();

        const int commandCount = 500;

        var records = new List<CommandRecord>(commandCount);

        for (var i = 0; i < commandCount; i++)
        {
            records.Add(service.Enqueue(
                $"Device-{i % 10}",
                $"Command-{i}"));
        }

        Assert(service.UniqueCommandCount == commandCount,
            "Unique ID count is incorrect after repeated insertions.");

        Assert(records.Select(record => record.Id).Distinct().Count()
                == commandCount,
            "Generated command IDs are not unique.");

        Assert(records.All(record => service.ContainsCommand(record.Id)),
            "One or more inserted IDs cannot be found.");

        for (var i = 0; i < 200; i++)
        {
            var processed = service.ProcessNext();

            Assert(processed is not null,
                "Expected a pending command to be processed.");

            Assert(!service.IsCommandPending(processed.Id),
                "Processed command remained pending.");
        }

        for (var i = 0; i < 100; i++)
        {
            var undone = service.UndoLast();

            Assert(undone is not null,
                "Expected a pending command to be undone.");

            Assert(!service.IsCommandPending(undone.Id),
                "Undone command remained pending.");
        }

        Assert(service.UniqueCommandCount == commandCount,
            "Processing and undo changed the unique ID count.");

        Assert(service.HistoryCount == commandCount,
            "Processing and undo changed historical record count.");

        Assert(service.QueueCount == commandCount - 300,
            "Pending count is inconsistent after processing and undo.");

        Assert(service.Queue.Select(record => record.Id).Distinct().Count()
                == service.QueueCount,
            "Duplicate IDs exist in the pending queue.");

        Assert(service.Queue.All(record => service.IsCommandPending(record.Id)),
            "A queued command is absent from the pending set.");

        Assert(records.All(record => service.ContainsCommand(record.Id)),
            "Historical command tracking lost an ID.");
    }

    private static void TestConcurrentSetConsistency()
    {
        var service = CreateService();

        const int workers = 8;
        const int commandsPerWorker = 100;
        const int expected = workers * commandsPerWorker;

        Parallel.For(0, workers, worker =>
        {
            for (var i = 0; i < commandsPerWorker; i++)
            {
                service.Enqueue(
                    $"Device-{worker}",
                    $"Concurrent-{worker}-{i}");
            }
        });

        Assert(service.UniqueCommandCount == expected,
            "Concurrent insertion produced an incorrect unique ID count.");

        var queueIds = service.Queue
            .Select(record => record.Id)
            .ToArray();

        Assert(queueIds.Length == expected,
            "Concurrent insertion produced an incorrect pending count.");

        Assert(queueIds.Distinct().Count() == expected,
            "Concurrent insertion produced duplicate command IDs.");

        Assert(queueIds.All(service.ContainsCommand),
            "A pending ID is missing from unique command tracking.");

        Assert(queueIds.All(service.IsCommandPending),
            "A queued ID is missing from pending membership.");

        Assert(service.History.Select(record => record.Id).Distinct().Count()
                == expected,
            "Concurrent insertion produced duplicate history IDs.");

        Assert(service.HistoryCount == expected,
            "Concurrent insertion produced an incorrect history count.");
    }

    private static void TestRepeatedOperations()
    {
        var service = CreateService();

        for (var i = 0; i < 250; i++)
        {
            service.Enqueue(
                $"D{i % 7}",
                $"Command-{i}",
                i % 3 == 0 ? "Critical" :
                i % 3 == 1 ? "High" : "Normal");
        }

        for (var i = 0; i < 75; i++)
        {
            Assert(service.ProcessNext() is not null,
                "Expected a command to be processed.");
        }

        for (var i = 0; i < 25; i++)
        {
            Assert(service.UndoLast() is not null,
                "Expected a pending command to be undone.");
        }

        Assert(service.QueueCount == service.Queue.Count,
            "Pending count differs from queue contents.");

        var pendingIds = service.Queue
            .Select(record => record.Id)
            .ToHashSet();

        Assert(pendingIds.Count == service.QueueCount,
            "Queue contains duplicate pending command IDs.");

        Assert(service.Queue.All(record => record.Status == "Queued"),
            "Queue contains a record with a non-pending status.");

        Assert(service.HistoryCount == 250,
            "Sorted timeline lost command records.");

        Assert(service.GetCommandTimeline().Count == 250,
            "Timeline query returned an incorrect number of records.");

        Assert(service.UniqueCommandCount == 250,
            "Unique command tracking lost or duplicated IDs.");

        Assert(service.Queue.All(record => service.IsCommandPending(record.Id)),
            "A queued command is missing from the pending set.");

        Assert(service.Queue.All(record => service.ContainsCommand(record.Id)),
            "A queued command is missing from unique command tracking.");
    }

    private static void TestConcurrentSubmissions()
    {
        var service = CreateService();

        const int workers = 8;
        const int commandsPerWorker = 100;

        Parallel.For(0, workers, worker =>
        {
            for (var i = 0; i < commandsPerWorker; i++)
            {
                service.Enqueue(
                    $"Device-{worker}",
                    $"Command-{worker}-{i}",
                    i % 2 == 0 ? "High" : "Normal");
            }
        });

        var expected = workers * commandsPerWorker;

        Assert(service.QueueCount == expected,
            "Concurrent submissions lost or duplicated pending commands.");

        Assert(service.HistoryCount == expected,
            "Concurrent submissions corrupted history.");

        Assert(service.Queue.Select(record => record.Id).Distinct().Count()
                == expected,
            "Pending queue contains duplicate command IDs.");

        Assert(service.UniqueCommandCount == expected,
            "Concurrent submissions produced an incorrect unique ID count.");

        Assert(service.Queue.All(record => service.IsCommandPending(record.Id)),
            "Concurrent submissions left queued IDs absent from pending membership.");

        Assert(service.GetCommandTimeline().Count == expected,
            "Concurrent submissions corrupted the sorted timeline.");

        for (var worker = 0; worker < workers; worker++)
        {
            Assert(
                service.GetCommandsForDevice($"Device-{worker}").Count
                    == commandsPerWorker,
                $"Device index returned an incorrect count for worker {worker}.");
        }
    }

    private static void TestConcurrentAlerts()
    {
        var service = CreateService();

        const int workers = 8;
        const int alertsPerWorker = 50;

        Parallel.For(0, workers, worker =>
        {
            for (var i = 0; i < alertsPerWorker; i++)
            {
                service.EnqueueCriticalAlert(
                    $"Sensor-{worker}",
                    $"Alert-{worker}-{i}");
            }
        });

        var expected = workers * alertsPerWorker;

        Assert(service.QueueCount == expected,
            "Concurrent alerts were lost or duplicated.");

        Assert(service.CriticalQueueCount == expected,
            "Not every alert received Critical priority.");

        Assert(service.UniqueCommandCount == expected,
            "Concurrent alerts produced an incorrect unique ID count.");

        for (var i = 0; i < expected; i++)
        {
            var record = service.ProcessNext();

            Assert(record is not null,
                "A critical alert could not be processed.");

            Assert(record.Priority == "Critical",
                "A non-critical command was selected ahead of a critical alert.");

            Assert(!service.IsCommandPending(record.Id),
                "A processed alert remained in the pending set.");
        }

        Assert(service.QueueCount == 0,
            "Pending alerts remain after processing.");

        Assert(service.HistoryCount == expected,
            "Processing alerts corrupted the sorted history.");

        Assert(service.UniqueCommandCount == expected,
            "Processing alerts changed the unique command count.");
    }

    private static void TestLookupWorkload()
    {
        var service = CreateService();

        const int deviceCount = 100;
        const int commandsPerDevice = 50;
        const int totalCommands = deviceCount * commandsPerDevice;

        for (var device = 0; device < deviceCount; device++)
        {
            for (var command = 0; command < commandsPerDevice; command++)
            {
                service.Enqueue(
                    $"Device-{device:D3}",
                    $"Command-{command:D3}");
            }
        }

        var lookupTimer = Stopwatch.StartNew();

        for (var i = 0; i < 1000; i++)
        {
            var deviceId = $"Device-{i % deviceCount:D3}";
            var commands = service.GetCommandsForDevice(deviceId);

            Assert(commands.Count == commandsPerDevice,
                $"Indexed lookup failed for {deviceId}.");
        }

        lookupTimer.Stop();

        var timelineTimer = Stopwatch.StartNew();
        var timeline = service.GetCommandTimeline(limit: 100);
        timelineTimer.Stop();

        Assert(service.HistoryCount == totalCommands,
            "Lookup workload produced an incorrect history count.");

        Assert(service.UniqueCommandCount == totalCommands,
            "Lookup workload produced an incorrect unique command count.");

        Assert(timeline.Count == 100,
            "Sorted timeline did not return the requested 100 records.");

        Assert(timeline.SequenceEqual(
                timeline.OrderByDescending(record => record.CreatedAt)
                    .ThenByDescending(record => record.Id)),
            "Sorted timeline returned records in the wrong order.");

        Assert(lookupTimer.ElapsedMilliseconds >= 0,
            "Device lookup timing was invalid.");

        Assert(timelineTimer.ElapsedMilliseconds >= 0,
            "Timeline query timing was invalid.");
    }
}
