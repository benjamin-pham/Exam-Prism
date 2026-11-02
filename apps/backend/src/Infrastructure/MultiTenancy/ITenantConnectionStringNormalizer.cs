namespace ExamPrism.Infrastructure.MultiTenancy;

public interface ITenantConnectionStringNormalizer
{
    string Normalize(string connectionString);
}
