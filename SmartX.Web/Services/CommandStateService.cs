using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace SmartX.Web.Services;

public sealed class CommandStateService
{
    private readonly object _sync = new();
    private readonly ILogger<CommandStateService>? _logger;

    private readonly Queue<CommandRecord> _queue = new();
    private readonly List<CommandRecord> _history = new();
    private readonly Stack<Guid> _undoStack = new();

    private readonly PriorityQueue<CommandRecord, (int Rank, long Sequence)>
        _priorityQueue = new();

    private readonly Dictionary<string, List<CommandRecord>> _commandsByDevice =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _activeErrors =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<CommandAuditEntry> _auditLog = new();

    private long _prioritySequence;
    private long _auditSequence;

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
                return _queue.ToArray();
        }
    }

    public IReadOnlyList<CommandRecord> History
    {
        get
        {
            lock (_sync)
                return _history.ToArray();
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

    public IReadOnlyList<CommandAuditEntry> AuditLog
    {
        get
        {
            lock (_sync)
            {
                return _auditLog
                    .OrderByDescending(entry => entry.Timestamp)
                    .ThenByDescending(entry => entry.Sequence)
                    .ToArray();
            }
        }
    }

    public int QueueCount
    {
        get
        {
            lock (_sync)
                return _queue.Count;
        }
    }

    public int HistoryCount
    {
        get
        {
            lock (_sync)
                return _history.Count;
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
                return _queue.Count(command =>
                    command.Status == "Queued" &&
                    command.Priority == "Critical");
        }
    }

    public CommandRecord Enqueue(
        string deviceId,
        string command,
        string? priority,
        string? parameters)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException(
                "A device ID is required.", nameof(deviceId));

        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException(
                "A command is required.", nameof(command));

        var record = new CommandRecord
        {
            DeviceId = deviceId.Trim(),
            Command = command.Trim(),
            Priority = NormalizePriority(priority),
            Parameters = parameters?.Trim() ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "Queued",
            Details = string.IsNullOrWhiteSpace(parameters)
                ? "Submitted to the local command queue."
                : $"Parameters: {parameters.Trim()}"
        };

        lock (_sync)
        {
            _queue.Enqueue(record);

            _priorityQueue.Enqueue(
                record,
                (GetPriorityRank(record.Priority), _prioritySequence++));

            _history.Insert(0, record);
            _undoStack.Push(record.Id);

            if (!_commandsByDevice.TryGetValue(
                record.DeviceId, out var commands))
            {
                commands = new List<CommandRecord>();
                _commandsByDevice.Add(record.DeviceId, commands);
            }

            commands.Add(record);

            AddAuditEntry(
                record,
                "Queued",
                $"Command '{record.Command}' queued with {record.Priority} priority.");
        }

        NotifyStateChanged();
        return record;
    }

    public CommandRecord? ProcessNext()
    {
        CommandRecord? record;

        lock (_sync)
        {
            if (!_queue.TryDequeue(out record))
                return null;

            if (record.Status != "Queued")
                return null;

            MarkProcessed(record, "FIFO");
            RemoveFromUndoStack(record.Id);
        }

        NotifyStateChanged();
        return record;
    }

    public CommandRecord? ProcessHighestPriority()
    {
        CommandRecord? record = null;

        lock (_sync)
        {
            while (_priorityQueue.TryDequeue(
                out var candidate, out _))
            {
                if (candidate.Status != "Queued")
                    continue;

                record = candidate;
                break;
            }

            if (record is null)
                return null;

            RemoveFromFifoQueue(record.Id);
            RemoveFromUndoStack(record.Id);
            MarkProcessed(record, "priority scheduling");
        }

        NotifyStateChanged();
        return record;
    }

    public CommandRecord? UndoLast()
    {
        CommandRecord? record = null;

        lock (_sync)
        {
            while (_undoStack.Count > 0)
            {
                var id = _undoStack.Pop();
                var candidate = _history.FirstOrDefault(
                    item => item.Id == id);

                if (candidate is null || candidate.Status != "Queued")
                    continue;

                record = candidate;
                break;
            }

            if (record is null)
                return null;

            RemoveFromFifoQueue(record.Id);

            record.Status = "Undone";
            record.Details =
                "Removed from the pending queue. No device action was reversed.";

            AddAuditEntry(
                record,
                "Undone",
                "Pending command removed by undo.");
        }

        NotifyStateChanged();
        return record;
    }

    public int ClearQueue()
    {
        int cleared;

        lock (_sync)
        {
            var pending = _queue.ToArray();
            cleared = pending.Length;

            foreach (var record in pending)
            {
                if (record.Status != "Queued")
                    continue;

                record.Status = "Cleared";
                record.Details =
                    "Removed from the queue before processing.";

                AddAuditEntry(
                    record,
                    "Cleared",
                    "Pending command removed during queue clearance.");
            }

            _queue.Clear();
            _priorityQueue.Clear();
            _undoStack.Clear();
        }

        if (cleared > 0)
            NotifyStateChanged();

        return cleared;
    }

    public IReadOnlyList<CommandRecord> GetCommandsForDevice(
        string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return Array.Empty<CommandRecord>();

        lock (_sync)
        {
            return _commandsByDevice.TryGetValue(
                deviceId.Trim(), out var commands)
                ? commands.ToArray()
                : Array.Empty<CommandRecord>();
        }
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

    public bool TryGetCommand(
        Guid commandId,
        out CommandRecord? record)
    {
        lock (_sync)
        {
            record = _history.FirstOrDefault(
                command => command.Id == commandId);

            return record is not null;
        }
    }

    public void ClearAuditLog()
    {
        lock (_sync)
        {
            if (_auditLog.Count == 0)
                return;

            _auditLog.Clear();
        }

        NotifyStateChanged();
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

    private void RemoveFromFifoQueue(Guid id)
    {
        var remaining = _queue
            .Where(command => command.Id != id)
            .ToArray();

        _queue.Clear();

        foreach (var command in remaining)
            _queue.Enqueue(command);
    }

    private void RemoveFromUndoStack(Guid id)
    {
        if (_undoStack.Count == 0)
            return;

        var remaining = _undoStack
            .Where(commandId => commandId != id)
            .Reverse()
            .ToArray();

        _undoStack.Clear();

        foreach (var commandId in remaining)
            _undoStack.Push(commandId);
    }

    private void AddAuditEntry(
        CommandRecord? record,
        string action,
        string details,
        string? errorKey = null)
    {
        _auditLog.Add(new CommandAuditEntry
        {
            Sequence = _auditSequence++,
            Timestamp = DateTimeOffset.UtcNow,
            CommandId = record?.Id,
            DeviceId = record?.DeviceId ?? string.Empty,
            Command = record?.Command ?? string.Empty,
            Action = action,
            Details = details,
            ErrorKey = errorKey
        });
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
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
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
