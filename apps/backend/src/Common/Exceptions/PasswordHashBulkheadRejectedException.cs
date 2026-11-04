namespace Common.Exceptions;

using System;

public class PasswordHashBulkheadRejectedException : Exception
{
    public PasswordHashBulkheadRejectedException()
        : base("Password hashing bulkhead capacity exceeded.")
    {
    }

    public PasswordHashBulkheadRejectedException(string message)
        : base(message)
    {
    }

    public PasswordHashBulkheadRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
