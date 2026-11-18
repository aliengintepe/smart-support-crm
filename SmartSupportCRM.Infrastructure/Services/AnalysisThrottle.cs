namespace SmartSupportCRM.Infrastructure.Services;

// One shared cooldown per application, so queued tickets do not hammer project-level quota.
public sealed class AnalysisThrottle(TimeProvider clock)
{
    private readonly object _sync = new();
    private DateTimeOffset _blockedUntil;
    public bool IsBlocked { get { lock (_sync) return clock.GetUtcNow() < _blockedUntil; } }
    public void Pause(TimeSpan delay)
    {
        lock (_sync)
        {
            var until = clock.GetUtcNow() + delay;
            if (until > _blockedUntil) _blockedUntil = until;
        }
    }
}
