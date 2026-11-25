// Port klasy WebClient z MegaApiClient 1.10.5 (https://github.com/gpailler/MegaApiClient, plik WebClient_HttpClient.cs),
// Copyright (c) Gilles Pailler, licencja MIT (https://github.com/gpailler/MegaApiClient/blob/v1.10.5/LICENSE).
// Zmiany: HttpClient na handlerze z kontekstu aplikacji (zamiast statycznego klienta), wywołania asynchroniczne
// blokowane przez GetAwaiter().GetResult() (adapter woła je w Task.Run), bez dyrektyw #if per TFM.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using CG.Web.MegaApiClient;

namespace ImageStudio.Storage.Mega;

public sealed class HttpClientWebClient : IWebClient
{
    private readonly HttpClient _httpClient;

    public HttpClientWebClient(HttpMessageHandler handler, TimeSpan timeout)
    {
        _httpClient = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        var assemblyName = typeof(IWebClient).Assembly.GetName();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(assemblyName.Name!, assemblyName.Version!.ToString(2)));
    }

    // ponytail: domyślny rozmiar bufora z biblioteki nieodczytany (1 MiB), wpływa tylko na porcje wysyłki
    public int BufferSize { get; set; } = 1024 * 1024;

    public string PostRequestJson(Uri url, string jsonData) => PostRequestJson(url, jsonData, null);

    public string PostRequestJson(Uri url, string jsonData, string? hashcash)
    {
        using var jsonStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonData));
        using var responseStream = PostRequest(url, jsonStream, "application/json", hashcash);
        return StreamToString(responseStream);
    }

    public string PostRequestRaw(Uri url, Stream dataStream)
    {
        using var responseStream = PostRequest(url, dataStream, "application/json", null);
        return StreamToString(responseStream);
    }

    public Stream PostRequestRawAsStream(Uri url, Stream dataStream) => PostRequest(url, dataStream, "application/octet-stream", null);

    public Stream GetRequestRaw(Uri url) => _httpClient.GetStreamAsync(url).GetAwaiter().GetResult();

    private Stream PostRequest(Uri url, Stream dataStream, string contentType, string? hashcash)
    {
        using var content = new StreamContent(dataStream, BufferSize);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (hashcash != null)
        {
            requestMessage.Headers.Add("X-Hashcash", hashcash);
        }

        var response = _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode
            && response.StatusCode == HttpStatusCode.InternalServerError
            && response.ReasonPhrase == "Server Too Busy")
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(((long)ApiResultCode.RequestFailedRetry).ToString()));
        }

        if (!response.IsSuccessStatusCode
            && response.StatusCode == HttpStatusCode.PaymentRequired
            && response.Headers.TryGetValues("X-Hashcash", out var hashcashValues))
        {
            var value = hashcashValues.First();
            var json = $"[{(long)ApiResultCode.HashcashRequired}, '{value}']";
            return new MemoryStream(Encoding.UTF8.GetBytes(json));
        }

        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
    }

    private static string StreamToString(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
