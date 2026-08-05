using System.Net;
using System.Net.Http;

namespace EpicSetup.Services;

internal static class Http
{
    public static HttpClient Create()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EpicSetup/1.0 (+https://github.com/epic-setup)");
        return client;
    }

    public static HttpClient Client { get; } = Create();
}
