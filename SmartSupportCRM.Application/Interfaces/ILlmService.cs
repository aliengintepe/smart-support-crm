using System.Threading.Tasks;
using SmartSupportCRM.Domain.Entities;

namespace SmartSupportCRM.Application.Interfaces;

public record LlmAnalysisResult(string? Summary, string? SuggestedDraft, SentimentType? Sentiment = null,
    TicketPriority? Priority = null, string? Category = null,
    AnalysisErrorCode ErrorCode = AnalysisErrorCode.None, string? Model = null, int? RetryAfterSeconds = null)
{
    public bool Succeeded => ErrorCode == AnalysisErrorCode.None && !string.IsNullOrWhiteSpace(Summary) &&
        !string.IsNullOrWhiteSpace(SuggestedDraft) && Sentiment is not null and not SentimentType.Unknown &&
        Priority != null && Category is "Billing" or "Account" or "Technical" or "General";
    public bool CanRetry => ErrorCode is AnalysisErrorCode.QuotaExceeded or AnalysisErrorCode.ServiceUnavailable or
        AnalysisErrorCode.Timeout or AnalysisErrorCode.NetworkError or AnalysisErrorCode.Truncated;
}

public interface ILlmService
{
    Task<LlmAnalysisResult> AnalyzeAndDraftReplyAsync(string ticketContent, CancellationToken cancellationToken = default);
}
