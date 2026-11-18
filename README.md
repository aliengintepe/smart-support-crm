# SmartSupportCRM

SmartSupportCRM is an ASP.NET Core MVC application that stores customer support tickets in SQL Server. It uses Gemini to summarize tickets, draft replies, and classify sentiment, priority, and category.

## Architecture

- **Domain** defines the ticket model, analysis statuses, and error codes.
- **Application** defines service interfaces for ticket operations and model requests.
- **Infrastructure** implements the SQL Server data layer, EF Core migrations, analysis queue, and Gemini client.
- **Web** contains the Razor views, MVC controllers, health endpoints, and background worker.
- **Tests** contains xUnit tests, an in-memory SQLite setup, and synthetic ONNX fixtures.

The application saves each ticket to SQL Server before analysis. A background worker picks up the ticket, sends its subject and body to Gemini, and saves the validated result to the same record. Temporary failures are retried with a delay. Suggested replies are drafts for a support agent to review.

## Run locally

You need Windows, the .NET 9 SDK, and SQL Server Express LocalDB. The default connection uses the current Windows user's MSSQLLocalDB instance.

Restore the solution's dependencies.

~~~powershell
dotnet restore SmartSupportCRM.sln
~~~

Install the EF Core 9 CLI tool if it is missing.

~~~powershell
dotnet tool install --global dotnet-ef --version 9.0.8
~~~

Apply the database migrations.

~~~powershell
dotnet ef database update --project SmartSupportCRM.Infrastructure --startup-project SmartSupportCRM.Web
~~~

Set the Gemini API key in the same PowerShell session used to start the application. In PowerShell 7, the following command masks the key as you enter it.

~~~powershell
$env:Gemini__ApiKey = Read-Host 'Gemini API key' -MaskInput
dotnet run --project SmartSupportCRM.Web --launch-profile http
~~~

Open [localhost:5071](http://localhost:5071). On Windows, run **.\Start-CRM.ps1 -CheckOnly** to check the LocalDB connection or **.\Start-CRM.ps1** to start the application. You can override the model names with **Gemini__Model** and **Gemini__FallbackModel**.

## Run tests

~~~powershell
dotnet test SmartSupportCRM.sln
~~~

The tests cover the ticket queue, model failure handling, and MVC behavior. The ONNX fixtures return fixed outputs for integration tests. The running application uses Gemini for ticket analysis.

## Demo scope

This project is a local portfolio demo. Ticket viewing and bulk deletion currently have no sign-in or role checks. Add access controls before deploying it to a server.
