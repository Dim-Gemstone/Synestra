using System.Threading.Channels;

namespace Synestra.Worker.Testing;

internal sealed class ControlledTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ControlledTimer> _timers = [];
    private readonly Dictionary<TimeSpan, Channel<bool>> _scheduled = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
    public override long GetTimestamp() { lock (_sync) return _timestamp; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public int ActiveTimers { get { lock (_sync) return _timers.Count; } }

    public async Task WaitForDelayAsync(TimeSpan duration, CancellationToken token)
    {
        Channel<bool> channel;
        lock (_sync) channel = Scheduled(duration);
        await channel.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);
    }

    public async Task WaitForTimersAsync(CancellationToken token, params TimeSpan[] remaining)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (remaining.GroupBy(value => value).All(group =>
                    _timers.Count(timer => timer.DueAt == _timestamp + group.Key.Ticks) >= group.Count())) return;
                changed = _changed.Task;
            }
            await changed.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
    }

    public void ShiftUtc(TimeSpan duration) { lock (_sync) _now += duration; }

    private Channel<bool> Scheduled(TimeSpan duration)
    {
        if (!_scheduled.TryGetValue(duration, out var channel))
            _scheduled.Add(duration, channel = Channel.CreateUnbounded<bool>());
        return channel;
    }
    private void Changed()
    {
        _changed.TrySetResult();
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Advance(TimeSpan duration)
    {
        List<ControlledTimer> due;
        lock (_sync)
        {
            _now += duration;
            _timestamp += duration.Ticks;
            due = _timers.Where(timer => timer.DueAt <= _timestamp).ToList();
            foreach (var timer in due) timer.DueAt = long.MaxValue;
            Changed();
        }
        foreach (var timer in due) timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot timers are expected.");
        lock (_sync)
        {
            var timer = new ControlledTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    private sealed class ControlledTimer(ControlledTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long DueAt { get; set; }
        private bool _disposed;
        public void Fire() { lock (owner._sync) { if (_disposed) return; } callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException();
            lock (owner._sync)
            {
                if (_disposed) return false;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._timestamp + dueTime.Ticks;
                owner.Scheduled(dueTime).Writer.TryWrite(true);
                owner.Changed();
                return true;
            }
        }
        public void Dispose() { lock (owner._sync) { _disposed = true; owner._timers.Remove(this); owner.Changed(); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
