namespace ExamPrism.Tests.Infrastructure.Modules;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Common.Modules;
using ExamPrism.Infrastructure.Modules;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class ModuleLoaderTests
{
    [Fact]
    public void DiscoverAndLoadModules_InstantiatesAndOrdersModulesByName_AndRejectsDuplicateNames()
    {
        var registry = ModuleLoader.DiscoverAndLoadModules([typeof(FakeAlphaModule).Assembly]);
        registry.Modules.Select(m => m.Name).Should().ContainInOrder("FakeAlpha", "FakeBeta");

        Action duplicateAct = () => ModuleLoader.InstantiateModules([typeof(FakeAlphaModule), typeof(DuplicateAlphaModule)]);
        duplicateAct.Should().Throw<InvalidOperationException>().WithMessage("*FakeAlpha*");
    }

    [Fact]
    public void AddModules_ThrowsFileNotFoundException_WhenModuleSettingFileIsMissing()
    {
        var builder = WebApplication.CreateBuilder();
        Action act = () => ModuleLoader.RegisterModules(builder, [new MissingSettingsModule()]);
        act.Should().Throw<FileNotFoundException>().WithMessage("*nonexistent.module.json*");
    }

    [Fact]
    public void AddModules_LoadsModuleSettingFiles_WithoutOverridingInMemoryTestSettings()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "appsettings.fakemodule.json"),
                """{"FakeSection": {"FromFile": "file-val", "OverriddenKey": "from-file"}}""");

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = tempDir.FullName });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FakeSection:OverriddenKey"] = "from-memory"
            });

            ModuleLoader.RegisterModules(builder, [new FakeSettingsModule()]);

            builder.Configuration["FakeSection:FromFile"].Should().Be("file-val");
            builder.Configuration["FakeSection:OverriddenKey"].Should().Be("from-memory");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AddModules_RegistersModuleRegistryInServices_AndExecutesModuleConfigureServices()
    {
        var builder = WebApplication.CreateBuilder();
        var registry = builder.AddModules(additionalAssemblies: [typeof(FakeConfiguringModule).Assembly]);

        registry.Modules.Should().Contain(m => m.Name == "FakeConfiguring");
        var sp = builder.Services.BuildServiceProvider();
        sp.GetService<ModuleRegistry>().Should().BeSameAs(registry);
        builder.Services.Should().Contain(s => s.ServiceType == typeof(IFakeMarkerService));
    }

    [Fact]
    public void InstantiateModules_ThrowsInvalidOperationException_WhenModuleNameIsWhitespace()
    {
        Action act = () => ModuleLoader.InstantiateModules([typeof(EmptyNameModule)]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*empty or whitespace*");
    }

    [Fact]
    public void DiscoverAndLoadModules_ReturnsEmptyRegistry_WhenSearchDirectoryHasNoModules()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var registry = ModuleLoader.DiscoverAndLoadModules(searchDirectory: tempDir.FullName);
            registry.Modules.Should().BeEmpty();
            registry.ModuleAssemblies.Should().BeEmpty();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    public interface IFakeMarkerService { }
    private sealed class FakeMarkerService : IFakeMarkerService { }

    public sealed class FakeConfiguringModule : IModule
    {
        public string Name => "FakeConfiguring";
        public string[] ModuleSettingFiles => [];
        public void ConfigureServices(WebApplicationBuilder builder)
        {
            builder.Services.AddSingleton<IFakeMarkerService, FakeMarkerService>();
        }
    }

    private sealed class EmptyNameModule : IModule
    {
        public string Name => "   ";
        public string[] ModuleSettingFiles => [];
        public void ConfigureServices(WebApplicationBuilder builder) { }
    }

    public sealed class FakeAlphaModule : IModule
    {
        public string Name => "FakeAlpha";
        public string[] ModuleSettingFiles => [];
        public void ConfigureServices(WebApplicationBuilder builder) { }
    }

    public sealed class FakeBetaModule : IModule
    {
        public string Name => "FakeBeta";
        public string[] ModuleSettingFiles => [];
        public void ConfigureServices(WebApplicationBuilder builder) { }
    }

    private sealed class DuplicateAlphaModule : IModule
    {
        public string Name => "FakeAlpha";
        public string[] ModuleSettingFiles => [];
        public void ConfigureServices(WebApplicationBuilder builder) { }
    }

    private sealed class MissingSettingsModule : IModule
    {
        public string Name => "MissingSettings";
        public string[] ModuleSettingFiles => ["nonexistent.module.json"];
        public void ConfigureServices(WebApplicationBuilder builder) { }
    }

    private sealed class FakeSettingsModule : IModule
    {
        public string Name => "FakeSettings";
        public string[] ModuleSettingFiles => ["appsettings.fakemodule.json"];
        public void ConfigureServices(WebApplicationBuilder builder) { }
    }
}
