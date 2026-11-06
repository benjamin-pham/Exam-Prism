namespace Common.Modules;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IStartupInitializer
{
    Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default);
}

