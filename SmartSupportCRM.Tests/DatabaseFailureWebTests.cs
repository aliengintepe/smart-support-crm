using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace SmartSupportCRM.Tests;

public class DatabaseFailureWebTests
{
    private static Process Start(int port, bool worker)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../SmartSupportCRM.Web"));
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet")
        { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "bin", config, "net9.0", "SmartSupportCRM.Web.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["Analysis__WorkerEnabled"] = worker.ToString();
        start.Environment["ConnectionStrings__DefaultConnection"] = "Server=127.0.0.1,1;Database=UnavailableTest;User ID=synthetic;Password=synthetic;Encrypt=False;Connect Timeout=1";
        return Process.Start(start)!;
    }

    [Fact]
    public async Task DatabaseOutageReturnsSafe503WhileLivenessAndStaticFormsStillWork()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var process = Start(port, false);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };
            var ready = false;
            for (var i = 0; i < 80; i++)
            {
                if (process.HasExited) throw new Exception(await stderr + await stdout);
                try { using var live = await client.GetAsync("/health/live"); ready = live.IsSuccessStatusCode; if (ready) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            Assert.True(ready);
            foreach (var path in new[] { "/Ticket/Index", "/Ticket/Details/1", "/Dashboard/GetSentimentStats", "/health/ready" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("Password=", body);
                Assert.DoesNotContain("SqlException", body);
            }
            using var form = await client.GetAsync("/Ticket/Create");
            Assert.Equal(HttpStatusCode.OK, form.StatusCode);
            using var liveAfterOutage = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, liveAfterOutage.StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    public async Task PortConflictDoesNotStartDatabaseWorkerOrCauseDisposedLoggerFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var process = Start(port, true);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotEqual(0, process.ExitCode);
            var output = await stdout + await stderr;
            Assert.Contains("address already in use", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Analysis worker unavailable", output);
            Assert.DoesNotContain("EventLogInternal", output);
            Assert.DoesNotContain("Database.Connection", output);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }
}
