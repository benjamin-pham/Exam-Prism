namespace ExamPrism.Infrastructure.Modules;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Common.Modules;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class ModuleLoader
{
    public static ModuleRegistry AddModules(
        this WebApplicationBuilder builder,
        IEnumerable<Assembly>? additionalAssemblies = null,
        string? searchDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var registry = DiscoverAndLoadModules(additionalAssemblies, searchDirectory);
        return RegisterModules(builder, registry.Modules);
    }

    public static ModuleRegistry DiscoverAndLoadModules(
        IEnumerable<Assembly>? additionalAssemblies = null,
        string? searchDirectory = null)
    {
        var directory = searchDirectory ?? AppContext.BaseDirectory;
        var loadedAssemblies = new List<Assembly>();

        if (Directory.Exists(directory))
        {
            var dllFiles = Directory.GetFiles(directory, "ExamPrism.Modules.*.dll");
            var currentDomainAssemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var dllPath in dllFiles)
            {
                var assemblyName = AssemblyName.GetAssemblyName(dllPath);
                var alreadyLoaded = currentDomainAssemblies.FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));

                var asm = alreadyLoaded ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
                loadedAssemblies.Add(asm);
            }
        }

        if (additionalAssemblies is not null)
        {
            loadedAssemblies.AddRange(additionalAssemblies);
        }

        var distinctAssemblies = loadedAssemblies.Distinct().ToList();

        var moduleTypes = new List<Type>();
        foreach (var assembly in distinctAssemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
            }
            catch
            {
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
                }
                catch
                {
                    types = [];
                }
            }

            foreach (var type in types)
            {
                if (type.IsClass && !type.IsAbstract && typeof(IModule).IsAssignableFrom(type))
                {
                    moduleTypes.Add(type);
                }
            }
        }

        var modules = InstantiateModules(moduleTypes);
        var moduleAssemblies = modules.Select(m => m.GetType().Assembly).Distinct().ToList();

        return new ModuleRegistry(modules, moduleAssemblies);
    }

    public static IReadOnlyList<IModule> InstantiateModules(IEnumerable<Type> moduleTypes)
    {
        ArgumentNullException.ThrowIfNull(moduleTypes);

        var modules = new List<IModule>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in moduleTypes)
        {
            if (Activator.CreateInstance(type, nonPublic: true) is not IModule module)
            {
                throw new InvalidOperationException($"Type '{type.FullName}' does not implement {nameof(IModule)}.");
            }

            if (string.IsNullOrWhiteSpace(module.Name))
            {
                throw new InvalidOperationException($"Module of type '{type.FullName}' has an empty or whitespace Name.");
            }

            if (!seenNames.Add(module.Name))
            {
                throw new InvalidOperationException($"Duplicate module name '{module.Name}' detected for type '{type.FullName}'.");
            }

            modules.Add(module);
        }

        return modules.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ModuleRegistry RegisterModules(WebApplicationBuilder builder, IEnumerable<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(modules);

        var moduleList = modules.ToList();

        var insertIndex = -1;
        for (var i = builder.Configuration.Sources.Count - 1; i >= 0; i--)
        {
            if (builder.Configuration.Sources[i] is JsonConfigurationSource)
            {
                insertIndex = i + 1;
                break;
            }
        }

        if (insertIndex < 0)
        {
            insertIndex = 0;
        }

        foreach (var module in moduleList)
        {
            if (module.ModuleSettingFiles is not null)
            {
                foreach (var settingFile in module.ModuleSettingFiles)
                {
                    if (string.IsNullOrWhiteSpace(settingFile))
                    {
                        continue;
                    }

                    string? resolvedPath = null;
                    var contentRootCandidate = Path.Combine(builder.Environment.ContentRootPath, settingFile);
                    if (File.Exists(contentRootCandidate))
                    {
                        resolvedPath = contentRootCandidate;
                    }
                    else
                    {
                        var baseDirCandidate = Path.Combine(AppContext.BaseDirectory, settingFile);
                        if (File.Exists(baseDirCandidate))
                        {
                            resolvedPath = baseDirCandidate;
                        }
                    }

                    if (resolvedPath is null)
                    {
                        throw new FileNotFoundException(
                            $"Module setting file '{settingFile}' for module '{module.Name}' not found in '{builder.Environment.ContentRootPath}' or '{AppContext.BaseDirectory}'.",
                            settingFile);
                    }

                    var baseSource = new JsonConfigurationSource
                    {
                        Path = resolvedPath,
                        Optional = false,
                        ReloadOnChange = false
                    };
                    baseSource.ResolveFileProvider();
                    builder.Configuration.Sources.Insert(insertIndex++, baseSource);

                    var ext = Path.GetExtension(resolvedPath);
                    var basePathWithoutExt = resolvedPath[..^ext.Length];
                    var envSpecificPath = $"{basePathWithoutExt}.{builder.Environment.EnvironmentName}{ext}";

                    var envSource = new JsonConfigurationSource
                    {
                        Path = envSpecificPath,
                        Optional = true,
                        ReloadOnChange = false
                    };
                    envSource.ResolveFileProvider();
                    builder.Configuration.Sources.Insert(insertIndex++, envSource);
                }
            }

            module.ConfigureServices(builder);
            builder.Services.AddValidatorsFromAssembly(module.GetType().Assembly, includeInternalTypes: true);
        }

        var moduleAssemblies = moduleList.Select(m => m.GetType().Assembly).Distinct().ToList();
        var registry = new ModuleRegistry(moduleList, moduleAssemblies);

        builder.Services.RemoveAll<ModuleRegistry>();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(new ModuleConfiguredMarker());

        return registry;
    }

    public static void ConfigureDiscoveredModules(
        IServiceCollection services,
        IConfiguration configuration,
        string? searchDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(s => s.ServiceType == typeof(ModuleConfiguredMarker)))
        {
            return;
        }

        var registry = DiscoverAndLoadModules(searchDirectory: searchDirectory);
        foreach (var module in registry.Modules)
        {
            module.ConfigureServices(services, configuration);
            services.AddValidatorsFromAssembly(module.GetType().Assembly, includeInternalTypes: true);
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name != null && name == "ExamPrism")
            {
                services.AddValidatorsFromAssembly(asm, includeInternalTypes: true);
            }
        }
        services.RemoveAll<ModuleRegistry>();
        services.AddSingleton(registry);
        services.AddSingleton(new ModuleConfiguredMarker());
    }
}

internal sealed class ModuleConfiguredMarker { }
