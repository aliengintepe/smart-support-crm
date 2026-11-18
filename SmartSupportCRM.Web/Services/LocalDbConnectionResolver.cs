using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SmartSupportCRM.Web.Services;

// LocalDB's registry can say Stopped while its SQL process still accepts connections.
// Resolve only a verified, current-user loopback pipe; never persist its transient name.
public sealed class LocalDbConnectionResolver(TimeProvider clock, ILogger<LocalDbConnectionResolver> logger)
{
    private readonly object _sync = new();
    private string? _original, _resolved;
    private DateTimeOffset _checkedAt;

    public string Resolve(string connectionString)
    {
        var original = new SqlConnectionStringBuilder(connectionString);
        var match = Regex.Match(original.DataSource, @"^\(localdb\)\\([a-zA-Z0-9_.-]+)$", RegexOptions.IgnoreCase);
        if (!OperatingSystem.IsWindows() || !original.IntegratedSecurity || !match.Success) return connectionString;
        lock (_sync)
        {
            if (_original == connectionString && clock.GetUtcNow() - _checkedAt < TimeSpan.FromSeconds(10)) return _resolved!;
            var previous = _resolved;
            _original = connectionString;
            _resolved = connectionString;
            _checkedAt = clock.GetUtcNow();
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Microsoft SQL Server Local DB", "Instances", match.Groups[1].Value, "error.log");
                if (!File.Exists(path)) return _resolved;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // Read only the tail; error.log may be large. SQL logs are UTF-16 with a BOM.
                var offset = Math.Max(0, file.Length - 32768);
                if (offset % 2 != 0) offset++;
                file.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(file, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
                var pipes = Regex.Matches(reader.ReadToEnd(), @"\\\\\.\\pipe\\LOCALDB#[A-Fa-f0-9]+\\tsql\\query");
                if (pipes.Count == 0) return _resolved;
                var candidate = new SqlConnectionStringBuilder(connectionString)
                {
                    DataSource = "np:" + pipes[^1].Value,
                    TrustServerCertificate = true,
                    ConnectTimeout = 3
                };
                using var probe = new SqlConnection(candidate.ConnectionString);
                probe.Open();
                candidate.ConnectTimeout = original.ConnectTimeout;
                _resolved = candidate.ConnectionString;
                if (_resolved != previous) logger.LogInformation("Verified current-user LocalDB pipe; automatic instance lookup bypassed for this runtime.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqlException)
            { /* No verified running pipe: retain normal LocalDB auto-start behavior. */ }
            return _resolved;
        }
    }
}
