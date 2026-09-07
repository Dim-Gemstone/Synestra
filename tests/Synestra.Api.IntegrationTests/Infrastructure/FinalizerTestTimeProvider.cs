using System.Threading.Channels;

namespace Synestra.Api.IntegrationTests.Infrastructure;

// The finalizer uses one-shot Task.Delay timers. Tests observe scheduling before advancing time.
internal sealed class FinalizerTestTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<DelayTimer> _timers = [];
    private readonly Channel<DelayTimer> _scheduled = Channel.CreateUnbounded<DelayTimer>();
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
    public int ScheduledCount { get { lock (_sync) return _timers.Count; } }

    public Task<DelayTimer> NextDelayAsync(CancellationToken token) =>
        _scheduled.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Tests expect one-shot delay timers.");
        lock (_sync)
        {
            var timer = new DelayTimer(this, callback, state, _now + dueTime);
            _timers.Add(timer);
            _scheduled.Writer.TryWrite(timer);
            return timer;
        }
    }

    public void Advance(TimeSpan elapsed)
    {
        List<DelayTimer> due;
        lock (_sync)
        {
            _now += elapsed;
            due = _timers.Where(timer => !timer.IsDisposed && !timer.Fired && timer.DueAt <= _now).ToList();
            foreach (var timer in due) timer.Fired = true;
        }
        foreach (var timer in due) timer.Fire();
    }

    internal sealed class DelayTimer(FinalizerTestTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; } = dueAt;
        public bool Fired { get; set; }
        public bool IsDisposed { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException();
            lock (owner._sync)
            {
                if (IsDisposed) return false;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime;
                Fired = false;
                return true;
            }
        }
        public void Dispose() { lock (owner._sync) IsDisposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
