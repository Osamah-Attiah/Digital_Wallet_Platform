using Microsoft.AspNetCore.Diagnostics;
using System.Text.Json;

namespace DigitalWallet.Api.Foundation.Errors;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if(exception is DigitalWallet.Transfers.Application.TransferException read)
        {
            var status=read.Code=="TRANSFER_NOT_FOUND"?404:read.Code is "CONCURRENCY_CONFLICT" or "IDEMPOTENCY_KEY_REUSED"?409:400;
            httpContext.Response.StatusCode=status;
            await httpContext.Response.WriteAsJsonAsync(ProblemDetailsFactory.Create(httpContext,status,read.Code,"The transfer request could not be completed."),cancellationToken);
            return true;
        }
        logger.LogError(new EventId(1002, "RequestFailed"), "{Module} {ErrorCode} {CorrelationId}", "Api", "INTERNAL_ERROR", ProblemDetailsFactory.GetCorrelationId(httpContext));

        var problemDetails = ProblemDetailsFactory.Create(
            httpContext,
            StatusCodes.Status500InternalServerError,
            "INTERNAL_ERROR",
            "An unexpected error occurred.");

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            problemDetails,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken);
        return true;
    }
}
