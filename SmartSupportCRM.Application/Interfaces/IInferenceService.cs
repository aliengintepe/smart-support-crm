using SmartSupportCRM.Domain.Entities;

namespace SmartSupportCRM.Application.Interfaces;

public record SentimentAnalysisResult(SentimentType Sentiment, float ConfidenceScore, TicketPriority Priority, string Category, string Source = "Rules");

public interface IInferenceService
{
    SentimentAnalysisResult ClassifyTicket(string text);
}
