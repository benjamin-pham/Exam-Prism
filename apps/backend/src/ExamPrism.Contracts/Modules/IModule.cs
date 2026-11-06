namespace Common.Modules;

using Microsoft.AspNetCore.Builder;

public interface IModule
{
    string Name { get; }
    string[] ModuleSettingFiles { get; }
    void ConfigureServices(WebApplicationBuilder builder);
}
