namespace ExamPrism.Infrastructure.Http;

public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    public bool EnableForwardedHeaders { get; set; } = true;
    public int ForwardLimit { get; set; } = 2;
    public bool TrustAllPrivateNetworks { get; set; } = true;
    public List<string> KnownProxies { get; set; } = [];
    public List<string> KnownNetworks { get; set; } = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];
}
