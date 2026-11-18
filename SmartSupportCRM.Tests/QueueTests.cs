using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartSupportCRM.Application.Interfaces;
using SmartSupportCRM.Domain.Entities;
using SmartSupportCRM.Infrastructure.Data;
using SmartSupportCRM.Infrastructure.Services;
using Xunit;

namespace SmartSupportCRM.Tests;

public class QueueTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private sealed class Model(params LlmAnalysisResult[] results) : ILlmService
    {
        private readonly Queue<LlmAnalysisResult> _results = new(results);
        public int Calls { get; private set; }
        public Task<LlmAnalysisResult> AnalyzeAndDraftReplyAsync(string text, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(_results.Dequeue()); }
    }
    private static LlmAnalysisResult Success() => new("Negative experience", "Please share more details", SentimentType.Negative,
        TicketPriority.Low, "General", Model: "test-model");
    private sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public ApplicationDbContext Db { get; }
        public Clock Clock { get; } = new();
        public AnalysisThrottle Throttle { get; }
        public Fixture()
        {
            Connection.Open();
            Db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(Connection).Options);
            Db.Database.EnsureCreated();
            Throttle = new(Clock);
        }
        public TicketService Service(ILlmService model) => new(Db, model, Throttle, Clock);
        public Task<Ticket> Create(TicketService service, string text = "It was very bad") => service.CreateTicketAsync(new Ticket
        { CustomerEmail = "test@example.com", Subject = "ticket", Content = text, IsResolved = true });
        public async Task<Ticket> Read(int id) { Db.ChangeTracker.Clear(); return (await Db.Tickets.FindAsync(id))!; }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    [Fact]
    public async Task CreatePersistsBeforeModelCallAndNeverUsesRules()
    {
        await using var f = new Fixture();
        var model = new Model(Success());
        var service = f.Service(model);
        var ticket = await f.Create(service, "critical urgent terrible broken");
        Assert.True(ticket.Id > 0);
        Assert.Equal(0, model.Calls);
        Assert.Equal(AnalysisStatus.Pending, ticket.AnalysisStatus);
        Assert.Equal(SentimentType.Unknown, ticket.Sentiment);
        Assert.Equal("Unclassified", ticket.Category);
        Assert.Null(ticket.AiSummary);
        Assert.True(await service.ProcessNextAnalysisAsync());
        var saved = await f.Read(ticket.Id);
        Assert.Equal(AnalysisStatus.Completed, saved.AnalysisStatus);
        Assert.Equal(SentimentType.Negative, saved.Sentiment);
        Assert.Equal(TicketPriority.Low, saved.Priority); // model result, no keyword override
        Assert.Equal("test-model", saved.AnalysisModel);
        Assert.Equal(0, saved.SentimentScore);
    }

    [Fact]
    public async Task ReanalysisDoesNotDuplicateJobsAndFailedRetryPreservesSuccessfulResult()
    {
        await using var f = new Fixture();
        var model = new Model(Success(), new(null, null, ErrorCode: AnalysisErrorCode.PermissionDenied));
        var service = f.Service(model);
        var ticket = await f.Create(service);
        var created = ticket.CreatedAt;
        Assert.False(await service.ReanalyzeTicketAsync(ticket.Id));
        Assert.False(await service.ReanalyzeTicketAsync(999));
        await service.ProcessNextAnalysisAsync();
        Assert.True(await service.ReanalyzeTicketAsync(ticket.Id));
        Assert.False(await service.ReanalyzeTicketAsync(ticket.Id));
        await service.ProcessNextAnalysisAsync();
        var saved = await f.Read(ticket.Id);
        Assert.Equal(AnalysisStatus.Failed, saved.AnalysisStatus);
        Assert.Equal(AnalysisErrorCode.PermissionDenied, saved.AnalysisErrorCode);
        Assert.Equal("Negative experience", saved.AiSummary);
        Assert.Equal("Please share more details", saved.SuggestedReply);
        Assert.True(saved.IsResolved);
        Assert.Equal(created, saved.CreatedAt);
        Assert.Single(await service.GetAllTicketsAsync());
    }

    [Fact]
    public async Task QuotaCooldownStopsOtherJobsAndEventuallyRecovers()
    {
        await using var f = new Fixture();
        var model = new Model(new(null, null, ErrorCode: AnalysisErrorCode.QuotaExceeded, RetryAfterSeconds: 90), Success(), Success());
        var service = f.Service(model);
        var first = await f.Create(service);
        var second = await f.Create(service);
        Assert.True(await service.ProcessNextAnalysisAsync());
        var failed = await f.Read(first.Id);
        Assert.Equal(AnalysisStatus.RetryScheduled, failed.AnalysisStatus);
        Assert.Equal(AnalysisErrorCode.QuotaExceeded, failed.AnalysisErrorCode);
        Assert.Equal(SentimentType.Unknown, failed.Sentiment);
        Assert.False(await service.ProcessNextAnalysisAsync());
        Assert.False(await service.ReanalyzeTicketAsync(first.Id));
        Assert.Equal(1, model.Calls);
        var afterRestart = new TicketService(f.Db, model, new AnalysisThrottle(f.Clock), f.Clock);
        Assert.False(await afterRestart.ProcessNextAnalysisAsync());
        f.Clock.Advance(91);
        Assert.True(await service.ProcessNextAnalysisAsync());
        Assert.True(await service.ProcessNextAnalysisAsync());
        Assert.Equal(AnalysisStatus.Completed, (await f.Read(first.Id)).AnalysisStatus);
        Assert.Equal(AnalysisStatus.Completed, (await f.Read(second.Id)).AnalysisStatus);
    }

    [Fact]
    public async Task RetryBudgetIsFiniteAndManualClicksRespectCooldown()
    {
        await using var f = new Fixture();
        var failures = Enumerable.Range(0, 5).Select(_ => new LlmAnalysisResult(null, null, ErrorCode: AnalysisErrorCode.Timeout)).ToArray();
        var model = new Model(failures);
        var service = f.Service(model);
        var ticket = await f.Create(service);
        for (var i = 0; i < 5; i++) { Assert.True(await service.ProcessNextAnalysisAsync()); f.Clock.Advance(i == 4 ? 0 : 601); }
        var saved = await f.Read(ticket.Id);
        Assert.Equal(AnalysisStatus.Failed, saved.AnalysisStatus);
        Assert.Equal(5, saved.AnalysisAttemptCount);
        Assert.False(await service.ReanalyzeTicketAsync(ticket.Id));
        Assert.False(await service.ProcessNextAnalysisAsync());
        Assert.Equal(5, model.Calls);
        f.Clock.Advance(601);
        Assert.True(await service.ReanalyzeTicketAsync(ticket.Id));
    }

    [Fact]
    public async Task AbandonedLeaseIsRecoveredAndResolveStateIsPreserved()
    {
        await using var f = new Fixture();
        var service = f.Service(new Model(Success()));
        var ticket = await f.Create(service);
        ticket.AnalysisStatus = AnalysisStatus.Processing;
        ticket.AnalysisLastAttemptAt = f.Clock.GetUtcNow().UtcDateTime.AddMinutes(-4);
        await f.Db.SaveChangesAsync();
        Assert.True(await service.ProcessNextAnalysisAsync());
        Assert.Equal(AnalysisStatus.Completed, (await f.Read(ticket.Id)).AnalysisStatus);
        await service.MarkAsResolvedAsync(ticket.Id);
        Assert.True((await f.Read(ticket.Id)).IsResolved);
        await service.ClearTicketsAsync();
        Assert.Empty(await service.GetAllTicketsAsync());
    }
}
