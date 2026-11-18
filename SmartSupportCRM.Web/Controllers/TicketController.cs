using Microsoft.AspNetCore.Mvc;
using SmartSupportCRM.Application.Interfaces;
using SmartSupportCRM.Domain.Entities;
using SmartSupportCRM.Web.Models;

namespace SmartSupportCRM.Web.Controllers;

public class TicketController : Controller
{
    private readonly ITicketService _ticketService;

    public TicketController(ITicketService ticketService)
    {
        _ticketService = ticketService;
    }

    public async Task<IActionResult> Index()
    {
        var tickets = await _ticketService.GetAllTicketsAsync();
        return View(tickets);
    }

    public IActionResult Create() => View();

    [HttpPost]
    public async Task<IActionResult> Create(CreateTicketViewModel input)
    {
        if (!ModelState.IsValid) return View(input);
        var ticket = new Ticket
        {
            CustomerEmail = input.CustomerEmail.Trim(),
            Subject = input.Subject.Trim(),
            Content = input.Content.Trim()
        };
        await _ticketService.CreateTicketAsync(ticket, HttpContext.RequestAborted);
        return RedirectToAction(nameof(Details), new { id = ticket.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var ticket = await _ticketService.GetTicketByIdAsync(id);
        if (ticket == null) return NotFound();
        return View(ticket);
    }

    [HttpPost]
    public async Task<IActionResult> Reanalyze(int id)
    {
        if (await _ticketService.GetTicketByIdAsync(id) == null) return NotFound();
        var queued = await _ticketService.ReanalyzeTicketAsync(id, HttpContext.RequestAborted);
        TempData["AnalysisMessage"] = queued
            ? "Analiz kuyruğa alındı. Sonuç hazır olduğunda sayfa güncellenecek."
            : "Analiz zaten kuyrukta veya bekleme süresi henüz dolmadı. Önceki sonuçlar korunuyor.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Resolve(int id)
    {
        if (await _ticketService.GetTicketByIdAsync(id) == null) return NotFound();
        await _ticketService.MarkAsResolvedAsync(id);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Clear()
    {
        await _ticketService.ClearTicketsAsync();
        return RedirectToAction(nameof(Index));
    }
}
