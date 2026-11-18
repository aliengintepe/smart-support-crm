using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace SmartSupportCRM.Web.Services;

public sealed class DatabaseExceptionFilter(ILogger<DatabaseExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.HttpContext.RequestAborted.IsCancellationRequested || DatabaseErrors.Find(context.Exception) is not { } db) return;
        logger.LogError("Database operation unavailable ({DatabaseErrorType}, {ErrorCode})", db.GetType().Name, DatabaseErrors.Code(db));
        context.HttpContext.Response.Headers.RetryAfter = "30";
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        var message = DatabaseErrors.Message(db);
        if (context.HttpContext.Request.Path.StartsWithSegments("/Dashboard/GetSentimentStats"))
            context.Result = new ObjectResult(new { error = "DatabaseUnavailable", message }) { StatusCode = 503 };
        else
            context.Result = new ViewResult
            {
                ViewName = "DatabaseUnavailable", StatusCode = 503,
                ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), context.ModelState) { Model = message }
            };
        context.ExceptionHandled = true;
    }
}
