namespace ExamPrism.Infrastructure.Identity;

using System;
using System.Threading;
using Common.Exceptions;
using Common.Telemetry;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public class BulkheadPasswordHasher<TUser> : IPasswordHasher<TUser>, IDisposable where TUser : class
{
    private readonly IPasswordHasher<TUser> _innerHasher;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxConcurrent;
    private readonly int _timeoutMs;
    private bool _disposed;

    [ActivatorUtilitiesConstructor]
    public BulkheadPasswordHasher(IOptions<IdentityHighLoadOptions> options)
        : this(options, new PasswordHasher<TUser>())
    {
    }

    public BulkheadPasswordHasher(
        IOptions<IdentityHighLoadOptions> options,
        IPasswordHasher<TUser> innerHasher)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(innerHasher);

        var opts = options.Value ?? new IdentityHighLoadOptions();
        _maxConcurrent = opts.MaxConcurrentPasswordHashes > 0
            ? opts.MaxConcurrentPasswordHashes
            : Math.Max(2, Environment.ProcessorCount);
        _timeoutMs = opts.PasswordHashQueueTimeoutMs > 0
            ? opts.PasswordHashQueueTimeoutMs
            : 2000;

        _semaphore = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);
        _innerHasher = innerHasher;
    }

    public string HashPassword(TUser user, string password)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_semaphore.Wait(_timeoutMs))
        {
            ExamPrismDiagnostics.RecordPasswordHashBulkheadRejection();
            throw new PasswordHashBulkheadRejectedException(
                $"Password hashing bulkhead capacity exceeded ({_maxConcurrent} concurrent).");
        }

        try
        {
            return _innerHasher.HashPassword(user, password);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public PasswordVerificationResult VerifyHashedPassword(TUser user, string hashedPassword, string providedPassword)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_semaphore.Wait(_timeoutMs))
        {
            ExamPrismDiagnostics.RecordPasswordHashBulkheadRejection();
            throw new PasswordHashBulkheadRejectedException(
                $"Password verification bulkhead capacity exceeded ({_maxConcurrent} concurrent).");
        }

        try
        {
            return _innerHasher.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _semaphore.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
