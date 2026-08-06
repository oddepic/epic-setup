using System.Net;
using System.Text.Json;
using EpicSetup.Models;
using EpicSetup.Services;
using Xunit;

namespace EpicSetup.Tests;

[Collection("CatalogService")]
public class CatalogServiceTests
{
    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        private readonly Catalog _catalog;

        public OkHandler(Catalog catalog) => _catalog = catalog;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(_catalog))
            });
    }

    [Fact]
    public void Embedded_force_shortcircuits_remote_and_cache()
    {
        var old = Environment.GetEnvironmentVariable("EPICSETUP_CATALOG_SOURCE");
        try
        {
            Environment.SetEnvironmentVariable("EPICSETUP_CATALOG_SOURCE", "embedded");
            using var http = new HttpClient(new FailHandler());
            var service = new CatalogService(http);

            var (catalog, source) = service.LoadSync();

            Assert.Equal(CatalogService.SourceKind.Embedded, source);
            Assert.Equal(48, CatalogFixture.AllApps(catalog).Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EPICSETUP_CATALOG_SOURCE", old);
        }
    }

    [Fact]
    public void Remote_success_is_reported_as_remote()
    {
        var old = Environment.GetEnvironmentVariable("EPICSETUP_CATALOG_SOURCE");
        try
        {
            Environment.SetEnvironmentVariable("EPICSETUP_CATALOG_SOURCE", null);
            var remoteCatalog = CatalogFixture.Load();
            using var http = new HttpClient(new OkHandler(remoteCatalog));
            var service = new CatalogService(http);

            var (catalog, source) = service.LoadSync();

            Assert.Equal(CatalogService.SourceKind.Remote, source);
            Assert.Equal(48, CatalogFixture.AllApps(catalog).Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EPICSETUP_CATALOG_SOURCE", old);
        }
    }

    [Fact]
    public void Remote_failure_falls_back_to_cache_or_embedded()
    {
        var old = Environment.GetEnvironmentVariable("EPICSETUP_CATALOG_SOURCE");
        try
        {
            Environment.SetEnvironmentVariable("EPICSETUP_CATALOG_SOURCE", null);
            using var http = new HttpClient(new FailHandler());
            var service = new CatalogService(http);

            var (catalog, source) = service.LoadSync();

            Assert.Contains(source, new[]
            {
                CatalogService.SourceKind.Cache,
                CatalogService.SourceKind.Embedded
            });
            Assert.True(CatalogFixture.AllApps(catalog).Count > 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EPICSETUP_CATALOG_SOURCE", old);
        }
    }

    [Fact]
    public void Embedded_loader_returns_repository_catalog()
    {
        var embedded = CatalogService.LoadEmbedded();
        var repo = CatalogFixture.Load();

        var embeddedApps = CatalogFixture.AllApps(embedded);
        var repoApps = CatalogFixture.AllApps(repo);

        Assert.Equal(repoApps.Count, embeddedApps.Count);
        Assert.Equal(repoApps.Select(a => a.Id).OrderBy(x => x),
            embeddedApps.Select(a => a.Id).OrderBy(x => x));
    }
}
