using System.Net;
using System.Net.Http;
using System.Text;
using CG.Web.MegaApiClient;
using ImageStudio.Storage.Mega;

namespace ImageStudio.Tests;

// Transport MEGA bez sieci: atrapa HttpMessageHandler odpowiada zadanym kodem i nagłówkami
public sealed class HttpClientWebClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(ct)));
            return respond(request);
        }
    }

    private static readonly Uri Url = new("https://g.api.mega.co.nz/cs?id=1");

    private static HttpClientWebClient Client(FakeHandler handler) => new(handler, TimeSpan.FromSeconds(30));

    [Fact]
    public void Post_json_sends_content_type_and_hashcash_header()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[0]", Encoding.UTF8, "application/json") });

        var reply = Client(handler).PostRequestJson(Url, "{\"a\":1}", "hash123");

        Assert.Equal("[0]", reply);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("hash123", request.Headers.GetValues("X-Hashcash").Single());
        Assert.Equal("{\"a\":1}", handler.Bodies.Single());
    }

    [Fact]
    public void Payment_required_with_hashcash_header_is_returned_as_the_api_error_array()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PaymentRequired);
            response.Headers.Add("X-Hashcash", "abc");
            return response;
        });

        var reply = Client(handler).PostRequestJson(Url, "{}");

        Assert.Equal($"[{(long)ApiResultCode.HashcashRequired}, 'abc']", reply);
    }

    [Fact]
    public void Server_too_busy_is_returned_as_the_retry_code()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = "Server Too Busy" });

        var reply = Client(handler).PostRequestJson(Url, "{}");

        Assert.Equal(((long)ApiResultCode.RequestFailedRetry).ToString(), reply);
    }

    [Fact]
    public void Requests_go_through_the_supplied_handler()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });

        Client(handler).PostRequestJson(Url, "{}");
        using var raw = Client(handler).GetRequestRaw(new Uri("https://example.test/file"));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://example.test/file", handler.Requests[1].RequestUri!.AbsoluteUri);
    }
}
