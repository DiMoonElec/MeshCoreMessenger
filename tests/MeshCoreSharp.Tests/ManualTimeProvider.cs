internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;
    private readonly List<ManualTimer> _timers = [];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan interval)
    {
        _ticks += interval.Ticks;
        foreach (var timer in _timers.ToArray())
            timer.FireIfDue();
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private long? _due;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed) return false;
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot timers are needed.");
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
            return true;
        }

        public void FireIfDue()
        {
            if (_disposed || _due is null || _due > clock._ticks) return;
            _due = null;
            callback(state);
        }

        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
