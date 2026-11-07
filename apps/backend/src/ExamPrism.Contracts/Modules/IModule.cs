namespace Common.Modules;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public interface IModule
{
    string Name { get; }
    string[] ModuleSettingFiles { get; }
    void ConfigureServices(WebApplicationBuilder builder);
    void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }
}

