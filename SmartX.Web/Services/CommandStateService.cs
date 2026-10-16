using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace SmartX.Web.Services;

public sealed class CommandStateService
{
    private readonly object _sync = new();
    private readonly ILogger<CommandStateService>? _logger;

    private readonly Queue<Guid> _fifoQueue = new();
    private readonly PriorityQueue<CommandRecord, (int Rank, long Sequence)>
        _priorityQueue = new();
    private readonly Stack<Guid> _undoStack = new();

    // O(1) command lookup by command ID.
    private readonly Dictionary<Guid, CommandRecord> _recordsById = new();

    // O(1) device lookup, with cached snapshots for repeated reads.
    private readonly Dictionary<string, List<CommandRecord>> _commandsByDevice =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IReadOnlyList<CommandRecord>>
        _deviceCommandCache =
            new(StringComparer.OrdinalIgnoreCase);

    // Chronologically sorted indexes for efficient timeline queries.
    private readonly SortedSet<CommandRecord> _timeline =
        new(Comparer<CommandRecord>.Create(CompareTimelineRecords));

    private readonly SortedSet<CommandAuditEntry> _auditTimeline =
        new(Comparer<CommandAuditEntry>.Create(CompareAuditEntries));

    private readonly HashSet<Guid> _pendingIds = new();
    private readonly HashSet<string> _activeErrors =
        new(StringComparer.OrdinalIgnoreCase);

    private long _prioritySequence;
    private long _auditSequence;

    private const int StaleEntryCompactionThreshold = 64;

    private int _staleFifoEntries;
    private int _stalePriorityEntries;
    private int _staleUndoEntries;

    public CommandStateService(
        ILogger<CommandStateService>? logger = null)
    {
        _logger = logger;
    }

    public event Action? StateChanged;

    public IReadOnlyList<CommandRecord> Queue
    {
        get
        {
            lock (_sync)
            {
                return _fifoQueue
                    .Where(_pendingIds.Contains)
                    .Select(id => _recordsById[id])
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Returns the newest commands first using the sorted timeline index.
    /// </summary>
    public IReadOnlyList<CommandRecord> History
    {
        get
        {
            lock (_sync)
                return _timeline.Reverse().ToArray();
        }
    }

    public IReadOnlyCollection<string> ActiveErrors
    {
        get
        {
            lock (_sync)
                return _activeErrors.ToArray();
        }
    }

    /// <summary>
    /// Returns audit entries newest first.
    /// </summary>
    public IReadOnlyList<CommandAuditEntry> AuditLog
    {
        get
        {
            lock (_sync)
                return _auditTimeline.Reverse().ToArray();
        }
    }

    public int QueueCount
    {
        get
        {
            lock (_sync)
                return _pendingIds.Count;
        }
    }

    public int HistoryCount
    {
        get
        {
            lock (_sync)
                return _timeline.Count;
        }
    }

    public int ActiveErrorCount
    {
        get
        {
            lock (_sync)
                return _activeErrors.Count;
        }
    }

    public int CriticalQueueCount
    {
        get
        {
            lock (_sync)
            {
                return _pendingIds.Count(id =>
                    _recordsById.TryGetValue(id, out var record) &&
                    record.Priority == "Critical");
            }
        }
    }

    public CommandRecord Enqueue(
        string deviceId,
        string command,
        string? priority = "Normal",
        string? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException(
                "A device ID is required.",
                nameof(deviceId));
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException(
                "A command is required.",
                nameof(command));
        }

        var normalizedDeviceId = deviceId.Trim();
        var normalizedCommand = command.Trim();
        var normalizedPriority = NormalizePriority(priority);
        var normalizedParameters = parameters?.Trim() ?? string.Empty;

        var record = new CommandRecord
        {
            DeviceId = normalizedDeviceId,
            Command = normalizedCommand,
            Priority = normalizedPriority,
            Parameters = normalizedParameters,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "Queued",
            Details = string.IsNullOrEmpty(normalizedParameters)
                ? "Submitted to the local command queue."
                : $"Parameters: {normalizedParameters}"
        };

        lock (_sync)
        {
            _recordsById.Add(record.Id, record);
            _pendingIds.Add(record.Id);

            _fifoQueue.Enqueue(record.Id);

            _priorityQueue.Enqueue(
                record,
                (GetPriorityRank(record.Priority), _prioritySequence++));

            _undoStack.Push(record.Id);

            // Maintain the sorted command timeline.
            _timeline.Add(record);

            // Update the device index and its cached snapshot.
            if (!_commandsByDevice.TryGetValue(
                    normalizedDeviceId,
                    out var deviceCommands))
            {
                deviceCommands = new List<CommandRecord>();
                _commandsByDevice.Add(normalizedDeviceId, deviceCommands);
            }

            deviceCommands.Add(record);
            _deviceCommandCache[normalizedDeviceId] =
                deviceCommands.ToArray();

            AddAuditEntry(
                record,
                "Queued",
                $"Command queued with {record.Priority} priority.");
        }

        NotifyStateChanged();
        return record;
    }

    public CommandRecord EnqueueCriticalAlert(
        string deviceId,
        string alertCommand,
        string? parameters = null)
    {
        return Enqueue(deviceId, alertCommand, "Critical", parameters);
    }

    /// <summary>
    /// Processes the highest-priority pending command.
    /// Critical precedes High, which precedes Normal.
    /// Equal-priority commands are processed FIFO.
    /// </summary>
    public CommandRecord? ProcessNext()
    {
        return ProcessHighestPriority();
    }

    public CommandRecord? ProcessHighestPriority()
    {
        CommandRecord? processed;

        lock (_sync)
        {
            processed = TakeNextPriorityCommand();

            if (processed is null)
                return null;

            InvalidateOtherEntries(processed.Id, "priority");

            MarkProcessed(processed, "priority scheduling");
            CompactStaleCollections();
        }

        NotifyStateChanged();
        return processed;
    }

    /// <summary>
    /// Processes the next pending command in FIFO order,
    /// regardless of priority.
    /// </summary>
    public CommandRecord? ProcessNextFifo()
    {
        CommandRecord? processed;

        lock (_sync)
        {
            processed = TakeNextFifoCommand();

            if (processed is null)
                return null;

            InvalidateOtherEntries(processed.Id, "fifo");

            MarkProcessed(processed, "FIFO");
            CompactStaleCollections();
        }

        NotifyStateChanged();
        return processed;
    }

    public CommandRecord? UndoLast()
    {
        CommandRecord? undone = null;

        lock (_sync)
        {
            while (_undoStack.Count > 0)
            {
                var id = _undoStack.Pop();

                if (!_pendingIds.Remove(id))
                {
                    DecrementIfPositive(ref _staleUndoEntries);
                    continue;
                }

                if (!_recordsById.TryGetValue(id, out undone))
                {
                    undone = null;
                    continue;
                }

                _staleFifoEntries++;
                _stalePriorityEntries++;

                undone.Status = "Undone";
                undone.Details =
                    "Removed from the pending queue. " +
                    "No device action was reversed.";

                AddAuditEntry(
                    undone,
                    "Undone",
                    "Most recently submitted pending command undone.");

                break;
            }

            CompactStaleCollections();
        }

        if (undone is not null)
            NotifyStateChanged();

        return undone;
    }

    public int ClearQueue()
    {
        int cleared;

        lock (_sync)
        {
            var pending = _pendingIds
                .Where(_recordsById.ContainsKey)
                .Select(id => _recordsById[id])
                .ToArray();

            cleared = pending.Length;

            foreach (var record in pending)
            {
                record.Status = "Cleared";
                record.Details =
                    "Removed from the queue before processing.";

                AddAuditEntry(
                    record,
                    "Cleared",
                    "Pending command removed during queue clearance.");
            }

            _pendingIds.Clear();
            _fifoQueue.Clear();
            _priorityQueue.Clear();
            _undoStack.Clear();

            _staleFifoEntries = 0;
            _stalePriorityEntries = 0;
            _staleUndoEntries = 0;
        }

        if (cleared > 0)
            NotifyStateChanged();

        return cleared;
    }

    /// <summary>
    /// Returns cached commands for a device using a case-insensitive
    /// dictionary lookup.
    /// </summary>
    public IReadOnlyList<CommandRecord> GetCommandsForDevice(
        string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return Array.Empty<CommandRecord>();

        lock (_sync)
        {
            var key = deviceId.Trim();

            return _deviceCommandCache.TryGetValue(key, out var cached)
                ? cached
                : Array.Empty<CommandRecord>();
        }
    }

    /// <summary>
    /// Returns commands in reverse chronological order within the
    /// requested range. The sorted index limits the timeline entries
    /// examined to the requested time range.
    /// </summary>
    public IReadOnlyList<CommandRecord> GetCommandTimeline(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? deviceId = null,
        int? limit = null)
    {
        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            throw new ArgumentException(
                "The start time cannot be later than the end time.");
        }

        if (limit.HasValue && limit.Value < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "The limit must be greater than zero.");
        }

        var deviceFilter = string.IsNullOrWhiteSpace(deviceId)
            ? null
            : deviceId.Trim();

        lock (_sync)
        {
            var lowerBound = CreateTimelineBoundary(
                from ?? DateTimeOffset.MinValue,
                Guid.Empty);

            var upperBound = CreateTimelineBoundary(
                to ?? DateTimeOffset.MaxValue,
                Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));

            var range = _timeline.GetViewBetween(
                lowerBound,
                upperBound);

            IEnumerable<CommandRecord> results = range.Reverse();

            if (deviceFilter is not null)
            {
                results = results.Where(record =>
                    string.Equals(
                        record.DeviceId,
                        deviceFilter,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (limit.HasValue)
                results = results.Take(limit.Value);

            return results.ToArray();
        }
    }

    public bool TryGetCommand(
        Guid commandId,
        out CommandRecord? record)
    {
        lock (_sync)
            return _recordsById.TryGetValue(commandId, out record);
    }

    public void RecordActiveError(string? errorKey)
    {
        if (string.IsNullOrWhiteSpace(errorKey))
            return;

        var key = errorKey.Trim();
        bool added;

        lock (_sync)
        {
            added = _activeErrors.Add(key);

            if (added)
            {
                AddAuditEntry(
                    null,
                    "Active error",
                    $"Active error recorded: {key}",
                    key);
            }
        }

        if (added)
            NotifyStateChanged();
    }

    public bool ResolveActiveError(string? errorKey)
    {
        if (string.IsNullOrWhiteSpace(errorKey))
            return false;

        var key = errorKey.Trim();
        bool removed;

        lock (_sync)
        {
            removed = _activeErrors.Remove(key);

            if (removed)
            {
                AddAuditEntry(
                    null,
                    "Error resolved",
                    $"Active error resolved: {key}",
                    key);
            }
        }

        if (removed)
            NotifyStateChanged();

        return removed;
    }

    public void ClearAuditLog()
    {
        bool changed;

        lock (_sync)
        {
            changed = _auditTimeline.Count > 0;
            _auditTimeline.Clear();
        }

        if (changed)
            NotifyStateChanged();
    }

    private CommandRecord? TakeNextPriorityCommand()
    {
        while (_priorityQueue.TryDequeue(out var candidate, out _))
        {
            if (!_pendingIds.Remove(candidate.Id))
            {
                DecrementIfPositive(ref _stalePriorityEntries);
                continue;
            }

            return candidate;
        }

        return null;
    }

    private CommandRecord? TakeNextFifoCommand()
    {
        while (_fifoQueue.Count > 0)
        {
            var id = _fifoQueue.Dequeue();

            if (!_pendingIds.Remove(id))
            {
                DecrementIfPositive(ref _staleFifoEntries);
                continue;
            }

            return _recordsById.TryGetValue(id, out var record)
                ? record
                : null;
        }

        return null;
    }

    private void InvalidateOtherEntries(Guid commandId, string mode)
    {
        if (mode == "priority")
            _staleFifoEntries++;
        else
            _stalePriorityEntries++;

        _staleUndoEntries++;
    }

    private void MarkProcessed(
        CommandRecord record,
        string processingMode)
    {
        record.Status = "Processed locally";
        record.Details =
            $"Removed from the queue using {processingMode}. " +
            "No physical device execution was performed.";

        AddAuditEntry(
            record,
            record.Status,
            $"Command processed locally using {processingMode}.");
    }

    private void CompactStaleCollections()
    {
        var threshold = Math.Max(
            StaleEntryCompactionThreshold,
            _pendingIds.Count);

        if (_staleFifoEntries > threshold)
        {
            var activeIds = _fifoQueue
                .Where(_pendingIds.Contains)
                .ToArray();

            _fifoQueue.Clear();

            foreach (var id in activeIds)
                _fifoQueue.Enqueue(id);

            _staleFifoEntries = 0;
        }

        if (_stalePriorityEntries > threshold)
        {
            var activeEntries = _priorityQueue.UnorderedItems
                .Where(item => _pendingIds.Contains(item.Element.Id))
                .Select(item => (
                    Record: item.Element,
                    Priority: item.Priority))
                .ToArray();

            _priorityQueue.Clear();

            foreach (var item in activeEntries)
            {
                _priorityQueue.Enqueue(
                    item.Record,
                    item.Priority);
            }

            _stalePriorityEntries = 0;
        }

        if (_staleUndoEntries > threshold)
        {
            var activeIds = _undoStack
                .Where(_pendingIds.Contains)
                .ToArray();

            _undoStack.Clear();

            for (var i = activeIds.Length - 1; i >= 0; i--)
                _undoStack.Push(activeIds[i]);

            _staleUndoEntries = 0;
        }
    }

    private void AddAuditEntry(
        CommandRecord? record,
        string action,
        string details,
        string? errorKey = null)
    {
        var entry = new CommandAuditEntry
        {
            Sequence = _auditSequence++,
            Timestamp = DateTimeOffset.UtcNow,
            CommandId = record?.Id,
            DeviceId = record?.DeviceId ?? string.Empty,
            Command = record?.Command ?? string.Empty,
            Action = action,
            Details = details,
            ErrorKey = errorKey
        };

        _auditTimeline.Add(entry);
    }

    private static CommandRecord CreateTimelineBoundary(
        DateTimeOffset timestamp,
        Guid id)
    {
        return new CommandRecord
        {
            CreatedAt = timestamp,
            Id = id
        };
    }

    private static int CompareTimelineRecords(
        CommandRecord? left,
        CommandRecord? right)
    {
        if (ReferenceEquals(left, right))
            return 0;

        if (left is null)
            return -1;

        if (right is null)
            return 1;

        var timestampComparison =
            left.CreatedAt.CompareTo(right.CreatedAt);

        return timestampComparison != 0
            ? timestampComparison
            : left.Id.CompareTo(right.Id);
    }

    private static int CompareAuditEntries(
        CommandAuditEntry? left,
        CommandAuditEntry? right)
    {
        if (ReferenceEquals(left, right))
            return 0;

        if (left is null)
            return -1;

        if (right is null)
            return 1;

        var timestampComparison =
            left.Timestamp.CompareTo(right.Timestamp);

        return timestampComparison != 0
            ? timestampComparison
            : left.Sequence.CompareTo(right.Sequence);
    }

    private static string NormalizePriority(string? priority) =>
        priority?.Trim().ToLowerInvariant() switch
        {
            "critical" => "Critical",
            "high" => "High",
            _ => "Normal"
        };

    private static int GetPriorityRank(string priority) =>
        priority switch
        {
            "Critical" => 0,
            "High" => 1,
            _ => 2
        };

    private static void DecrementIfPositive(ref int count)
    {
        if (count > 0)
            count--;
    }

    private void NotifyStateChanged()
    {
        var handlers = StateChanged;

        if (handlers is null)
            return;

        foreach (var callback in handlers.GetInvocationList())
        {
            if (callback is not Action handler)
                continue;

            try
            {
                handler.Invoke();
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "A CommandStateService StateChanged subscriber failed.");
            }
        }
    }
}

public sealed class CommandRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string DeviceId { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    public string Priority { get; init; } = "Normal";

    public string Parameters { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } =
        DateTimeOffset.UtcNow;

    public string Status { get; set; } = "Queued";

    public string Details { get; set; } = string.Empty;
}

public sealed class CommandAuditEntry
{
    public long Sequence { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public Guid? CommandId { get; init; }

    public string DeviceId { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public string? ErrorKey { get; init; }
}