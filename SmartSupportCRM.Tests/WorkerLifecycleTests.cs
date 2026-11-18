using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSupportCRM.Web.Services;
using SmartSupportCRM.Application.Interfaces;
using SmartSupportCRM.Domain.Entities;
using Xunit;

namespace SmartSupportCRM.Tests;

public class WorkerLifecycleTests
{
    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void Start() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() { _started.Dispose(); _stopping.Dispose(); _stopped.Dispose(); }
    }
    private sealed class Scopes : IServiceScopeFactory
    {
        public int Calls;
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref Calls);
            Called.TrySetResult();
            throw new InvalidOperationException("Synthetic scope failure.");
        }
    }

    private sealed class SyntheticDatabaseException : System.Data.Common.DbException { }
    private sealed class RecoveringScopes(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int Calls;
        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref Calls) == 1) throw new SyntheticDatabaseException();
            return inner.CreateScope();
        }
    }
    private sealed class IdleService : ITicketService
    {
        public TaskCompletionSource Processed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> ProcessNextAnalysisAsync(CancellationToken cancellationToken = default)
        { Processed.TrySetResult(); return Task.FromResult(false); }
        public Task<IEnumerable<Ticket>> GetAllTicketsAsync() => throw new NotSupportedException();
        public Task<Ticket?> GetTicketByIdAsync(int id) => throw new NotSupportedException();
        public Task<Ticket> CreateTicketAsync(Ticket ticket, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ReanalyzeTicketAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearTicketsAsync() => throw new NotSupportedException();
        public Task MarkAsResolvedAsync(int id) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ConnectionFailureBacksOffAndHealthRecoversWithoutRestart()
    {
        using var lifetime = new Lifetime();
        var idle = new IdleService();
        using var provider = new ServiceCollection().AddScoped<ITicketService>(_ => idle).BuildServiceProvider();
        var scopes = new RecoveringScopes(provider.GetRequiredService<IServiceScopeFactory>());
        var health = new AnalysisWorkerHealth(TimeProvider.System);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Analysis:DatabaseRetrySeconds"] = "1" }).Build();
        using var worker = new TicketAnalysisWorker(scopes, config, NullLogger<TicketAnalysisWorker>.Instance, lifetime, health);
        await worker.StartAsync(CancellationToken.None);
        lifetime.Start();
        for (var i = 0; i < 50 && health.Current.Available; i++) await Task.Delay(10);
        Assert.False(health.Current.Available);
        Assert.Equal(1, scopes.Calls);
        Assert.False(idle.Processed.Task.IsCompleted);
        await idle.Processed.Task.WaitAsync(TimeSpan.FromSeconds(4));
        lifetime.StopApplication();
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(health.Current.Available);
        Assert.Null(health.Current.Message);
        Assert.NotNull(health.Current.LastSuccess);
        Assert.Equal(2, scopes.Calls);
    }

    [Fact]
    public async Task FailedHostStartupNeverStartsDatabaseWork()
    {
        using var lifetime = new Lifetime();
        var scopes = new Scopes();
        using var worker = new TicketAnalysisWorker(scopes, new ConfigurationBuilder().Build(),
            NullLogger<TicketAnalysisWorker>.Instance, lifetime, new AnalysisWorkerHealth(TimeProvider.System));
        await worker.StartAsync(CancellationToken.None);
        Assert.Equal(0, scopes.Calls);
        lifetime.StopApplication();
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, scopes.Calls);
    }

    [Fact]
    public async Task SuccessfulHostStartupEnablesWorkerAndShutdownCompletes()
    {
        using var lifetime = new Lifetime();
        var scopes = new Scopes();
        using var worker = new TicketAnalysisWorker(scopes, new ConfigurationBuilder().Build(),
            NullLogger<TicketAnalysisWorker>.Instance, lifetime, new AnalysisWorkerHealth(TimeProvider.System));
        await worker.StartAsync(CancellationToken.None);
        Assert.Equal(0, scopes.Calls);
        lifetime.Start();
        await scopes.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lifetime.StopApplication();
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, scopes.Calls);
    }
}
