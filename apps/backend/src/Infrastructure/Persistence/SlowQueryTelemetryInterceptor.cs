namespace ExamPrism.Infrastructure.Persistence;

using System.Data.Common;
using Common.MultiTenancy;
using Common.Telemetry;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class SlowQueryTelemetryInterceptor(
    IOptions<PersistenceOptions> options,
    ILogger<SlowQueryTelemetryInterceptor> logger,
    IMultiTenantContextAccessor<ExamPrismTenantInfo>? tenantAccessor = null) : DbCommandInterceptor
{
    private readonly PersistenceOptions _options = options?.Value ?? new PersistenceOptions();
    private readonly ILogger<SlowQueryTelemetryInterceptor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private string? ResolveTenantId()
    {
        var tenantInfo = tenantAccessor?.MultiTenantContext?.TenantInfo;
        if (tenantInfo is null)
        {
            return null;
        }

        return tenantInfo.TenantGuid != Guid.Empty
            ? tenantInfo.TenantGuid.ToString("D")
            : (string.IsNullOrWhiteSpace(tenantInfo.Id) ? null : tenantInfo.Id);
    }

    public void RecordIfSlow(string? commandText, TimeSpan duration, string? tenantId = null)
    {
        if (duration.TotalMilliseconds >= _options.SlowQueryThresholdMs)
        {
            tenantId ??= ResolveTenantId();

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

    public override void CommandFailed(
        DbCommand command,
        CommandErrorEventData eventData)
    {
        RecordIfSlow(command?.CommandText, eventData.Duration);
        base.CommandFailed(command!, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        RecordIfSlow(command?.CommandText, eventData.Duration);
        return base.CommandFailedAsync(command!, eventData, cancellationToken);
    }
}
