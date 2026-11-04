namespace ExamPrism.Infrastructure.Persistence;

using System.Data.Common;
using Common.Telemetry;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class SlowQueryTelemetryInterceptor(
    IOptions<PersistenceOptions> options,
    ILogger<SlowQueryTelemetryInterceptor> logger) : DbCommandInterceptor
{
    private readonly PersistenceOptions _options = options?.Value ?? new PersistenceOptions();
    private readonly ILogger<SlowQueryTelemetryInterceptor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void RecordIfSlow(string? commandText, TimeSpan duration, string? tenantId = null)
    {
        if (duration.TotalMilliseconds >= _options.SlowQueryThresholdMs)
        {
            _logger.LogWarning(
                "Slow query detected ({ElapsedMs:F2}ms exceeding threshold {ThresholdMs}ms): {CommandText}",
                duration.TotalMilliseconds,
                _options.SlowQueryThresholdMs,
                commandText);

            ExamPrismDiagnostics.RecordSlowQuery(commandText, duration.TotalMilliseconds, tenantId);
        }
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        RecordIfSlow(command?.CommandText, eventData.Duration);
        return base.ReaderExecutedAsync(command!, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        RecordIfSlow(command?.CommandText, eventData.Duration);
        return base.NonQueryExecutedAsync(command!, eventData, result, cancellationToken);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        RecordIfSlow(command?.CommandText, eventData.Duration);
        return base.ScalarExecutedAsync(command!, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        RecordIfSlow(command?.CommandText, eventData.Duration);
        return base.ReaderExecuted(command!, eventData, result);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        RecordIfSlow(command?.CommandText, eventData.Duration);
        return base.NonQueryExecuted(command!, eventData, result);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        RecordIfSlow(command?.CommandText, eventData.Duration);
        return base.ScalarExecuted(command!, eventData, result);
    }
}
