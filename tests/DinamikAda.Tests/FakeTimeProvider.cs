namespace DinamikAda.Tests;

public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    private long _ticks;

    public FakeTimeProvider(DateTimeOffset? initialUtc = null)
    {
        _utcNow = initialUtc ?? new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        _ticks = 1_000_000_000L;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan delta)
    {
        _utcNow = _utcNow.Add(delta);
        _ticks += delta.Ticks;
    }
}
