param(
    [ValidateRange(1024, 65535)][int]$Port = 5071,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SmartSupportCRM.Web'
$config = Get-Content -LiteralPath (Join-Path $project 'appsettings.json') -Raw | ConvertFrom-Json
$previousConnection = [Environment]::GetEnvironmentVariable('ConnectionStrings__DefaultConnection', 'Process')
$previousEnvironment = [Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Process')
$connection = if ($previousConnection) { $previousConnection } else { $config.ConnectionStrings.DefaultConnection }
$sql = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($connection)
try {
    if ($sql.DataSource -match '^\(localdb\)\\(.+)$') {
        $instance = $Matches[1]
        $info = (& SqlLocalDB info $instance | Out-String)
        $pipe = [regex]::Match($info, 'np:[^\r\n]+').Value.Trim()
        if (-not $pipe) {
            # LocalDB's control API can report Stopped while its SQL process is still serving.
            # Match only this user's exact instance log path, not another SQL installation.
            $instanceLog = Join-Path $env:LOCALAPPDATA "Microsoft\Microsoft SQL Server Local DB\Instances\$instance\error.log"
            $servers = Get-CimInstance Win32_Process -Filter "Name='sqlservr.exe'" -ErrorAction SilentlyContinue
            foreach ($server in $servers) {
                if ($server.ExecutablePath -like '*\LocalDB\Binn\sqlservr.exe' -and
                    $server.CommandLine.IndexOf($instanceLog, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $name = [regex]::Match($server.CommandLine, 'LOCALDB#[A-Fa-f0-9]+').Value
                    if ($name) { $pipe = "np:\\.\pipe\$name\tsql\query"; break }
                }
            }
        }
        if (-not $pipe) {
            & SqlLocalDB start $instance
            $info = (& SqlLocalDB info $instance | Out-String)
            $pipe = [regex]::Match($info, 'np:[^\r\n]+').Value.Trim()
        }
        if (-not $pipe) { throw 'LocalDB başlatılamadı veya bağlantı yolu okunamadı. Aynı Windows kullanıcısının normal terminalinde çalıştırın.' }
        # Resolve afresh on every launch; never persist an instance-specific pipe name.
        $sql['Data Source'] = $pipe
        $sql['TrustServerCertificate'] = $true
        $sql['Connect Timeout'] = 10
        $connection = $sql.ConnectionString
    }
    $probe = New-Object System.Data.SqlClient.SqlConnection($connection)
    try { $probe.Open(); Write-Output 'Veritabanı bağlantısı başarılı.' }
    finally { $probe.Dispose() }
    if ($CheckOnly) { return }
    if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
        throw "Port $Port kullanımda. Mevcut CRM'yi çalıştırdığınız terminalde Ctrl+C ile durdurun veya -Port ile farklı bir port seçin."
    }
    [Environment]::SetEnvironmentVariable('ConnectionStrings__DefaultConnection', $connection, 'Process')
    if (-not $previousEnvironment) { [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Development', 'Process') }
    & dotnet run --project $project --no-launch-profile --urls "http://localhost:$Port"
    if ($LASTEXITCODE -ne 0) { throw "CRM başlatılamadı; çıkış kodu $LASTEXITCODE." }
}
finally {
    [Environment]::SetEnvironmentVariable('ConnectionStrings__DefaultConnection', $previousConnection, 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $previousEnvironment, 'Process')
}
