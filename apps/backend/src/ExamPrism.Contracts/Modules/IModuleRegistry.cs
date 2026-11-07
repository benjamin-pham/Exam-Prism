namespace Common.Modules;

using System.Collections.Generic;
using System.Reflection;

public interface IModuleRegistry
{
    IReadOnlyList<IModule> Modules { get; }
    IReadOnlyList<Assembly> ModuleAssemblies { get; }
}
