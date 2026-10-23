using System.Net;
using System.Text;

namespace MediaOrganizer.Core.Tests;

/// <summary>Servidor HTTP falso para os provedores de legenda: rotas por método + prefixo de URL (host incluído).</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly List<Route> _routes = [];

    public List<(HttpMethod Method, string Url, string? Body, HttpRequestMessage Request)> Requests { get; } = [];

    private sealed record Route(HttpMethod Method, string Prefix, Func<HttpResponseMessage> Respond)
    {
        public int Hits { get; set; }
    }

    public FakeHttp Json(HttpMethod method, string urlPrefix, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, urlPrefix, () => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public FakeHttp Bytes(string urlPrefix, byte[] content) =>
        On(HttpMethod.Get, urlPrefix, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });

    public FakeHttp On(HttpMethod method, string urlPrefix, Func<HttpResponseMessage> respond)
    {
        _routes.Add(new Route(method, urlPrefix, respond));
        return this;
    }

    public IEnumerable<string> Urls => Requests.Select(r => r.Url);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, url, body, request));

        // A rota mais específica (prefixo mais longo) vence.
        var route = _routes
            .Where(r => r.Method == request.Method && url.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Prefix.Length)
            .FirstOrDefault();
        if (route is null) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };

        route.Hits++;
        return route.Respond();
    }
}
