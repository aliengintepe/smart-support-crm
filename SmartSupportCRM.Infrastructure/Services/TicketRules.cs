using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SmartSupportCRM.Application.Interfaces;
using SmartSupportCRM.Domain.Entities;

namespace SmartSupportCRM.Infrastructure.Services;

// Conservative routing estimates; no calibrated model confidence is claimed.
public static class TicketRules
{
    public static SentimentAnalysisResult Classify(string text)
    {
        var normalized = Normalize(text);
        bool Has(string pattern) => Regex.IsMatch(normalized, @"\b(?:" + pattern + @")\b", RegexOptions.CultureInvariant);
        var negative = Has("bad|terrible|broken|error|failed|awful|unhappy|not working|not good|not helpful|not resolved|kotu|berbat|hata|bozuk|calismiyor|cozulmedi|memnun degilim|yavas");
        var positive = Has("great|excellent|helpful|good|thanks|resolved|awesome|tesekkur|harika|mukemmel|cozuldu|memnunum");
        var sentiment = negative ? SentimentType.Negative : positive ? SentimentType.Positive : SentimentType.Neutral;
        var priority = Has("critical|security breach|data loss|outage|veri kaybi|guvenlik ihlali|sistem coktu") ? TicketPriority.Critical :
            Has("urgent|blocked|acil|erisim yok|giris yapamiyorum") ? TicketPriority.High : TicketPriority.Medium;
        var category = Has("refund|billing|payment|invoice|odeme|fatura|iade") ? "Billing" :
            Has("login|password|account|giris|sifre|hesap") ? "Account" :
            Has("bug|error|broken|hata|calismiyor|bozuk") ? "Technical" : "General";
        return new(sentiment, 0, priority, category, "Rules");
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.ToLowerInvariant().Replace('ı', 'i').Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString();
    }
}
