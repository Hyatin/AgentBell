namespace AgentBell.Desktop.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _gate = new();
    private readonly HashSet<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = utcNow;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        ManualTimer[] dueTimers;
        lock (_gate)
        {
            _utcNow += elapsed;
            _timestamp = checked(_timestamp + elapsed.Ticks);
            dueTimers = _timers
                .Where(timer => timer.DueTimestamp <= _timestamp)
                .ToArray();
            foreach (var timer in dueTimers)
            {
                if (timer.PeriodTicks == Timeout.InfiniteTimeSpan.Ticks)
                {
                    _timers.Remove(timer);
                }
                else
                {
                    timer.DueTimestamp = checked(_timestamp + timer.PeriodTicks);
                }
            }
        }

        foreach (var timer in dueTimers)
        {
            timer.Fire();
        }
    }

    private bool ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        ValidateDuration(dueTime, nameof(dueTime));
        ValidateDuration(period, nameof(period));
        lock (_gate)
        {
            _timers.Remove(timer);
            timer.PeriodTicks = period.Ticks;
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.DueTimestamp = checked(_timestamp + dueTime.Ticks);
                _timers.Add(timer);
            }

            return true;
        }
    }

    private void RemoveTimer(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private static void ValidateDuration(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private int _disposed;

        public long DueTimestamp { get; set; }

        public long PeriodTicks { get; set; } = Timeout.InfiniteTimeSpan.Ticks;

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            Volatile.Read(ref _disposed) == 0 && owner.ChangeTimer(this, dueTime, period);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.RemoveTimer(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                callback(state);
            }
        }
    }
}
