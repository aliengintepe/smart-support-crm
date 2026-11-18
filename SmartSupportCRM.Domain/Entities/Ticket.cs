using System;

namespace SmartSupportCRM.Domain.Entities;

public enum TicketPriority
{
    Low,
    Medium,
    High,
    Critical
}

public enum SentimentType
{
    Positive,
    Neutral,
    Negative,
    Unknown
}

public enum AnalysisStatus { Pending, Processing, Completed, RetryScheduled, Failed }
public enum AnalysisErrorCode
{
    None, MissingConfiguration, Authentication, PermissionDenied, ModelUnavailable,
    QuotaExceeded, ServiceUnavailable, Timeout, NetworkError, InvalidResponse, Blocked, Truncated
}

public class Ticket
{
    public int Id { get; set; }
    public string CustomerEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public SentimentType Sentiment { get; set; } = SentimentType.Unknown;
    public float SentimentScore { get; set; }
    public TicketPriority Priority { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? AiSummary { get; set; }
    public string? SuggestedReply { get; set; }
    public string? AssignedDepartment { get; set; }
    public bool IsResolved { get; set; }
    public AnalysisStatus AnalysisStatus { get; set; } = AnalysisStatus.Pending;
    public AnalysisErrorCode AnalysisErrorCode { get; set; }
    public string? AnalysisModel { get; set; }
    public int AnalysisAttemptCount { get; set; }
    public DateTime? AnalysisLastAttemptAt { get; set; }
    public DateTime? AnalysisNextAttemptAt { get; set; }
}
