using Microsoft.EntityFrameworkCore;
using SmartSupportCRM.Domain.Entities;

namespace SmartSupportCRM.Infrastructure.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Ticket> Tickets { get; set; }
}
