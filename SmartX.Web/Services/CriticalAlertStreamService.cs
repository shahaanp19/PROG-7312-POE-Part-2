
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SmartX.Web.Services;

public sealed class CriticalAlertStreamService : IAsyncDisposable
{
    private const int DefaultCapacity = 1024;

    private readonly Channel<CriticalAlertMessage> _channel;
    private readonly CommandStateService _commandState;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _consumerTask;

    private long _acceptedCount;
    private long _processedCount;
    private long _failedCount;
    private long _totalProcessingTicks;
    private long _maxProcessingTicks;
    private int _disposed;

    public CriticalAlertStreamService(CommandStateService commandState)
    {
        _commandState = commandState
            ?? throw new ArgumentNullException(nameof(commandState));

        _channel = Channel.CreateBounded<CriticalAlertMessage>(
            new BoundedChannelOptions(DefaultCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        _consumerTask = ConsumeAsync(_shutdown.Token);
    }

    public int Capacity => DefaultCapacity;

    public long AcceptedCount => Interlocked.Read(ref _acceptedCount);

    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    public long FailedCount => Interlocked.Read(ref _failedCount);

    public long AverageProcessingMicroseconds
    {
        get
        {
            var count = ProcessedCount;

            return count == 0
                ? 0
                : (long)(
                    Interlocked.Read(ref _totalProcessingTicks)
                    * 1_000_000d
                    / Stopwatch.Frequency
                    / count);
        }
    }

    public long MaxProcessingMicroseconds =>
        (long)(
            Interlocked.Read(ref _maxProcessingTicks)
            * 1_000_000d
            / Stopwatch.Frequency);

    public bool TrySubmit(
        string deviceId,
        string alertCommand,
        string? parameters = null)
    {
        ThrowIfDisposed();
        Validate(deviceId, alertCommand);

        var message = CreateMessage(deviceId, alertCommand, parameters);

        if (!_channel.Writer.TryWrite(message))
            return false;

        Interlocked.Increment(ref _acceptedCount);
        return true;
    }

    public async ValueTask SubmitAsync(
        string deviceId,
        string alertCommand,
        string? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Validate(deviceId, alertCommand);

        var message = CreateMessage(deviceId, alertCommand, parameters);

        await _channel.Writer.WriteAsync(message, cancellationToken)
            .ConfigureAwait(false);

        Interlocked.Increment(ref _acceptedCount);
    }

    private static CriticalAlertMessage CreateMessage(
        string deviceId,
        string alertCommand,
        string? parameters)
    {
        return new CriticalAlertMessage(
            deviceId.Trim(),
            alertCommand.Trim(),
            parameters?.Trim(),
            DateTimeOffset.UtcNow);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var alert in _channel.Reader.ReadAllAsync(
                cancellationToken).ConfigureAwait(false))
            {
                var started = Stopwatch.GetTimestamp();

                try
                {
                    _commandState.EnqueueCriticalAlert(
                        alert.DeviceId,
                        alert.Command,
                        alert.Parameters);

                    var elapsed = Stopwatch.GetTimestamp() - started;

                    Interlocked.Increment(ref _processedCount);
                    Interlocked.Add(ref _totalProcessingTicks, elapsed);
                    UpdateMaximumProcessingTicks(elapsed);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failedCount);

                    Console.Error.WriteLine(
                        $"Critical alert ingestion failed for " +
                        $"{alert.DeviceId} at {alert.ReceivedAt:O}: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Expected during disposal.
        }
    }

    private void UpdateMaximumProcessingTicks(long elapsed)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _maxProcessingTicks);

            if (elapsed <= current)
                return;

            if (Interlocked.CompareExchange(
                    ref _maxProcessingTicks,
                    elapsed,
                    current) == current)
            {
                return;
            }
        }
    }

    private static void Validate(string deviceId, string alertCommand)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException(
                "Device ID is required.",
                nameof(deviceId));
        }

        if (string.IsNullOrWhiteSpace(alertCommand))
        {
            throw new ArgumentException(
                "Alert command is required.",
                nameof(alertCommand));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _channel.Writer.TryComplete();

        try
        {
            // Drain accepted alerts before shutting down the consumer.
            await _consumerTask.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }

    private sealed record CriticalAlertMessage(
        string DeviceId,
        string Command,
        string? Parameters,
        DateTimeOffset ReceivedAt);
}
