using System.Collections.Generic;
using System.Threading.Tasks;
using SmartSupportCRM.Domain.Entities;

namespace SmartSupportCRM.Application.Interfaces;

public interface ITicketService
{
    Task<IEnumerable<Ticket>> GetAllTicketsAsync();
    Task<Ticket?> GetTicketByIdAsync(int id);
    Task<Ticket> CreateTicketAsync(Ticket ticket, CancellationToken cancellationToken = default);
    Task<bool> ReanalyzeTicketAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> ProcessNextAnalysisAsync(CancellationToken cancellationToken = default);
    Task ClearTicketsAsync();
    Task MarkAsResolvedAsync(int id);
}
