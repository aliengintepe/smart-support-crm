using SmartSupportCRM.Application.Interfaces;

namespace SmartSupportCRM.Web.Services;

public sealed class TicketAnalysisWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<TicketAnalysisWorker> logger, IHostApplicationLifetime lifetime, AnalysisWorkerHealth health) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Analysis:WorkerEnabled", true)) return;
        // Do not touch SQL or the provider before Kestrel has successfully bound its port.
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        stoppingToken = shutdown.Token;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        try { await started.Task.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        var failures = 0;
        var baseDelay = configuration.GetValue("Analysis:DatabaseRetrySeconds", 30);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(2);
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ITicketService>();
                var processed = await service.ProcessNextAnalysisAsync(stoppingToken);
                if (failures > 0) logger.LogInformation("Analysis worker connection recovered.");
                failures = 0;
                health.Succeeded();
                if (processed) continue;
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                delay = AnalysisWorkerHealth.FailureDelay(failures, baseDelay);
                var databaseError = DatabaseErrors.Find(ex);
                health.Failed(databaseError == null ? "Analiz servisi geçici olarak kullanılamıyor." : DatabaseErrors.Message(databaseError));
                logger.LogWarning("Analysis worker unavailable ({FailureType}, code {ErrorCode}); retrying in {DelaySeconds}s",
                    ex.GetType().Name, databaseError == null ? null : (int?)DatabaseErrors.Code(databaseError), delay.TotalSeconds);
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
