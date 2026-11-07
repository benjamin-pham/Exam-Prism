namespace ExamPrism.Infrastructure.Persistence;

using System;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

public static class NpgsqlOptionsExtensions
{
    public static NpgsqlDbContextOptionsBuilder ConfigureNpgsql(
        this NpgsqlDbContextOptionsBuilder npgsqlOptions,
        PersistenceOptions persistenceOptions)
    {
        ArgumentNullException.ThrowIfNull(npgsqlOptions);
        ArgumentNullException.ThrowIfNull(persistenceOptions);

        npgsqlOptions.CommandTimeout(persistenceOptions.CommandTimeoutSeconds);
        npgsqlOptions.MaxBatchSize(persistenceOptions.MaxBatchSize);
        if (persistenceOptions.MaxRetryCount > 0)
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: persistenceOptions.MaxRetryCount,
                maxRetryDelay: TimeSpan.FromSeconds(persistenceOptions.MaxRetryDelaySeconds),
                errorCodesToAdd: null);
        }

        return npgsqlOptions;
    }
}
