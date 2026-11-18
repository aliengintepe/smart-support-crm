using Microsoft.EntityFrameworkCore;
using SmartSupportCRM.Application.Interfaces;
using SmartSupportCRM.Infrastructure.Data;
using SmartSupportCRM.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add<SmartSupportCRM.Web.Services.DatabaseExceptionFilter>();
});

// Configuration
builder.Services.Configure<SentimentModelSettings>(
    builder.Configuration.GetSection("SentimentModel"));

// Database
builder.Services.AddSingleton<SmartSupportCRM.Web.Services.LocalDbConnectionResolver>();
builder.Services.AddDbContext<ApplicationDbContext>((services, options) =>
    options.UseSqlServer(services.GetRequiredService<SmartSupportCRM.Web.Services.LocalDbConnectionResolver>()
        .Resolve(builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection is required."))));

// DI Services
builder.Services.AddHttpClient<ILlmService, GeminiLlmService>(client =>
    client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AnalysisThrottle>();
builder.Services.AddSingleton<SmartSupportCRM.Web.Services.AnalysisWorkerHealth>();
builder.Services.AddScoped<ITicketService, TicketService>();
builder.Services.AddHostedService<SmartSupportCRM.Web.Services.TicketAnalysisWorker>();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" }));
app.MapGet("/health/ready", async (ApplicationDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        if (!await db.Database.CanConnectAsync(cancellationToken) ||
            (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            return Results.Json(new { status = "DatabaseUnavailableOrMigrationPending" }, statusCode: 503);
        return Results.Ok(new { status = "Ready" });
    }
    catch (Exception) when (!cancellationToken.IsCancellationRequested)
    { return Results.Json(new { status = "DatabaseUnavailable" }, statusCode: 503); }
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Ticket}/{action=Index}/{id?}");

app.Run();

