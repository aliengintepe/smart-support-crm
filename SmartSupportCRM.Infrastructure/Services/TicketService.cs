using Microsoft.EntityFrameworkCore;
using SmartSupportCRM.Application.Interfaces;
using SmartSupportCRM.Domain.Entities;
using SmartSupportCRM.Infrastructure.Data;

namespace SmartSupportCRM.Infrastructure.Services;

public class TicketService(ApplicationDbContext context, ILlmService llmService, AnalysisThrottle throttle,
    TimeProvider clock) : ITicketService
{
    public async Task<IEnumerable<Ticket>> GetAllTicketsAsync() =>
        await context.Tickets.AsNoTracking().OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).ToListAsync();

    public async Task<Ticket?> GetTicketByIdAsync(int id) => await context.Tickets.FindAsync(id);

    public async Task<Ticket> CreateTicketAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.Content);
        // Persist first; the worker owns model calls and survives browser disconnects.
        ticket.Sentiment = SentimentType.Unknown;
        ticket.SentimentScore = 0;
        ticket.Category = "Unclassified";
        ticket.Priority = TicketPriority.Medium;
        ticket.AiSummary = null;
        ticket.SuggestedReply = null;
        ticket.AnalysisStatus = AnalysisStatus.Pending;
        ticket.AnalysisErrorCode = AnalysisErrorCode.None;
        ticket.AnalysisModel = null;
        ticket.AnalysisAttemptCount = 0;
        ticket.AnalysisLastAttemptAt = null;
        ticket.AnalysisNextAttemptAt = null;
        context.Tickets.Add(ticket);
        await context.SaveChangesAsync(cancellationToken);
        return ticket;
    }

    public async Task<bool> ReanalyzeTicketAsync(int id, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // Atomic scheduling prevents double-clicks from creating duplicate model requests.
        return await context.Tickets.Where(t => t.Id == id &&
            (t.AnalysisStatus == AnalysisStatus.Completed || t.AnalysisStatus == AnalysisStatus.Failed) &&
            (t.AnalysisNextAttemptAt == null || t.AnalysisNextAttemptAt <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AnalysisStatus, AnalysisStatus.Pending)
                .SetProperty(t => t.AnalysisAttemptCount, 0)
                .SetProperty(t => t.AnalysisErrorCode, AnalysisErrorCode.None)
                .SetProperty(t => t.AnalysisNextAttemptAt, (DateTime?)null), cancellationToken) == 1;
    }

    public async Task<bool> ProcessNextAnalysisAsync(CancellationToken cancellationToken = default)
    {
        if (throttle.IsBlocked) return false;
        var now = clock.GetUtcNow().UtcDateTime;
        // Honor persisted project cooldown even after an application restart.
        if (await context.Tickets.AnyAsync(t =>
            (t.AnalysisErrorCode == AnalysisErrorCode.QuotaExceeded || t.AnalysisErrorCode == AnalysisErrorCode.ServiceUnavailable ||
             t.AnalysisErrorCode == AnalysisErrorCode.Timeout || t.AnalysisErrorCode == AnalysisErrorCode.NetworkError) &&
            t.AnalysisNextAttemptAt > now, cancellationToken)) return false;
        var leaseExpired = now.AddMinutes(-3);
        // The processing lease recovers jobs interrupted by application shutdown.
        var eligible = context.Tickets.Where(t =>
            ((t.AnalysisStatus == AnalysisStatus.Pending || t.AnalysisStatus == AnalysisStatus.RetryScheduled) &&
                (t.AnalysisNextAttemptAt == null || t.AnalysisNextAttemptAt <= now)) ||
            (t.AnalysisStatus == AnalysisStatus.Processing && t.AnalysisLastAttemptAt < leaseExpired));
        var id = await eligible.OrderBy(t => t.AnalysisLastAttemptAt).ThenBy(t => t.CreatedAt)
            .Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken);
        if (id == null) return false;
        // Claim with a conditional SQL update; multiple workers cannot claim the same ticket.
        var claimed = await eligible.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AnalysisStatus, AnalysisStatus.Processing)
            .SetProperty(t => t.AnalysisLastAttemptAt, now)
            .SetProperty(t => t.AnalysisAttemptCount, t => t.AnalysisAttemptCount + 1), cancellationToken);
        if (claimed == 0) return false;
        var ticket = await context.Tickets.AsNoTracking().SingleAsync(t => t.Id == id, cancellationToken);
        var result = await llmService.AnalyzeAndDraftReplyAsync(ticket.Subject + "\n\n" + ticket.Content, cancellationToken);
        var finishedAt = clock.GetUtcNow().UtcDateTime;
        // Updating only analysis fields preserves simultaneous resolve/customer edits.
        var ownLease = context.Tickets.Where(t => t.Id == id && t.AnalysisStatus == AnalysisStatus.Processing &&
            t.AnalysisLastAttemptAt == now && t.AnalysisAttemptCount == ticket.AnalysisAttemptCount);
        if (result.Succeeded)
        {
            await ownLease.ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Sentiment, result.Sentiment!.Value)
                .SetProperty(t => t.SentimentScore, 0f)
                .SetProperty(t => t.Priority, result.Priority!.Value)
                .SetProperty(t => t.Category, result.Category!)
                .SetProperty(t => t.AiSummary, result.Summary)
                .SetProperty(t => t.SuggestedReply, result.SuggestedDraft)
                .SetProperty(t => t.AnalysisModel, result.Model)
                .SetProperty(t => t.AnalysisErrorCode, AnalysisErrorCode.None)
                .SetProperty(t => t.AnalysisStatus, AnalysisStatus.Completed)
                .SetProperty(t => t.AnalysisNextAttemptAt, (DateTime?)null), cancellationToken);
        }
        else
        {
            var error = result.ErrorCode == AnalysisErrorCode.None ? AnalysisErrorCode.InvalidResponse : result.ErrorCode;
            var retry = result.CanRetry && ticket.AnalysisAttemptCount < 5;
            var seconds = Math.Max(result.RetryAfterSeconds ?? 0, Math.Min(600, 30 * (1 << Math.Min(5, ticket.AnalysisAttemptCount - 1))));
            var next = retry || result.CanRetry ? finishedAt.AddSeconds(seconds) : (DateTime?)null;
            // Quota and infrastructure failures affect other queued jobs too.
            if (result.CanRetry) throttle.Pause(TimeSpan.FromSeconds(seconds));
            await ownLease.ExecuteUpdateAsync(s => s
                .SetProperty(t => t.AnalysisErrorCode, error)
                .SetProperty(t => t.AnalysisModel, t => t.AiSummary == null ? result.Model : t.AnalysisModel)
                .SetProperty(t => t.AnalysisStatus, retry ? AnalysisStatus.RetryScheduled : AnalysisStatus.Failed)
                .SetProperty(t => t.AnalysisNextAttemptAt, next), cancellationToken);
        }
        return true;
    }

    public async Task MarkAsResolvedAsync(int id) => await context.Tickets.Where(t => t.Id == id)
        .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsResolved, true));

    public async Task ClearTicketsAsync() => await context.Tickets.ExecuteDeleteAsync();
}
