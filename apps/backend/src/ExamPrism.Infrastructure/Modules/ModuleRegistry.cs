namespace ExamPrism.Infrastructure.Modules;

using System;
using System.Collections.Generic;
using System.Reflection;
using Common.Modules;

public sealed class ModuleRegistry
{
    public ModuleRegistry(IReadOnlyList<IModule> modules, IReadOnlyList<Assembly> moduleAssemblies)
    {
        Modules = modules ?? throw new ArgumentNullException(nameof(modules));
        ModuleAssemblies = moduleAssemblies ?? throw new ArgumentNullException(nameof(moduleAssemblies));
    }

    public IReadOnlyList<IModule> Modules { get; }
    public IReadOnlyList<Assembly> ModuleAssemblies { get; }
}

