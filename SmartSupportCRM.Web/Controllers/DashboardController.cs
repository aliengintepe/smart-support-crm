using Microsoft.AspNetCore.Mvc;
using SmartSupportCRM.Application.Interfaces;

namespace SmartSupportCRM.Web.Controllers;

public class DashboardController : Controller
{
    private readonly ITicketService _ticketService;

    public DashboardController(ITicketService ticketService)
    {
        _ticketService = ticketService;
    }

    public IActionResult Index() => View();

    public async Task<IActionResult> GetSentimentStats()
    {
        var all = await _ticketService.GetAllTicketsAsync();
        var tickets = all.Where(t => t.AiSummary != null && t.SuggestedReply != null && t.Sentiment != Domain.Entities.SentimentType.Unknown).ToList();
        var stats = new
        {
            Positive = tickets.Count(t => t.Sentiment == Domain.Entities.SentimentType.Positive),
            Neutral = tickets.Count(t => t.Sentiment == Domain.Entities.SentimentType.Neutral),
            Negative = tickets.Count(t => t.Sentiment == Domain.Entities.SentimentType.Negative),
            HighPriority = tickets.Count(t => t.Priority >= Domain.Entities.TicketPriority.High),
            PendingAnalysis = all.Count(t => t.AnalysisStatus != Domain.Entities.AnalysisStatus.Completed)
        };
        return Json(stats);
    }
}
