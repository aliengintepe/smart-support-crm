namespace SmartSupportCRM.Web.Services;

public record WorkerHealthSnapshot(bool Available, string? Message, DateTimeOffset? LastSuccess);

public sealed class AnalysisWorkerHealth(TimeProvider clock)
{
    private WorkerHealthSnapshot _snapshot = new(true, null, null);
    public WorkerHealthSnapshot Current => Volatile.Read(ref _snapshot);
    public void Succeeded() => Volatile.Write(ref _snapshot, new(true, null, clock.GetUtcNow()));
    public void Failed(string message) => Volatile.Write(ref _snapshot, new(false, message, Current.LastSuccess));

    public static TimeSpan FailureDelay(int failures, int baseSeconds = 30) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Clamp(baseSeconds, 1, 300) * (1 << Math.Clamp(failures - 1, 0, 4))));
}
