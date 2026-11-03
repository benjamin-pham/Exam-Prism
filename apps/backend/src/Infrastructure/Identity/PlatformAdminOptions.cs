namespace ExamPrism.Infrastructure.Identity;

public class PlatformAdminOptions
{
    public const string SectionName = "PlatformAdmin";

    public bool SeedOnStartup { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
