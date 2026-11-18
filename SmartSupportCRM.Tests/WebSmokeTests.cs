using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Xunit;

namespace SmartSupportCRM.Tests;

public class WebSmokeTests
{
    [Fact]
    public async Task WebRendersFormsRejectsCsrfAndValidatesBeforeSaving()
    {
        var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../SmartSupportCRM.Web"));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = webRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(webRoot, "bin", configuration, "net9.0", "SmartSupportCRM.Web.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        // No real provider requests or customer data are used by this test.
        start.Environment["Gemini__ApiKey"] = "";
        start.Environment["Analysis__WorkerEnabled"] = "false";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            var ready = false;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (process.HasExited) throw new InvalidOperationException(await stderr + await stdout);
                try
                {
                    using var response = await client.GetAsync("/Home/Privacy");
                    response.EnsureSuccessStatusCode();
                    ready = true;
                    break;
                }
                catch (HttpRequestException) { await Task.Delay(250); }
            }
            Assert.True(ready, "Web server did not become ready.");
            var html = await client.GetStringAsync("/Ticket/Create");
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            Assert.False(string.IsNullOrWhiteSpace(token));
            using var rejected = await client.PostAsync("/Ticket/Clear", new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var rejectedReanalysis = await client.PostAsync("/Ticket/Reanalyze/1", new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.BadRequest, rejectedReanalysis.StatusCode);
            using var invalid = await client.PostAsync("/Ticket/Create", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["CustomerEmail"] = "not-an-email", ["Subject"] = "", ["Content"] = ""
            }));
            Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
            Assert.Contains("validation-summary-errors", await invalid.Content.ReadAsStringAsync());
            using var dashboard = await client.GetAsync("/Dashboard");
            Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }
}
