using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant_App.UI;

public sealed class RemoteImageLoader : IDisposable
{
    private const int MaximumBytes = 512_000;
    private readonly HttpClient _client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<string> LoadAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!AnswerMarkdownFormatter.CanFetchInlineImage(uri))
            throw new InvalidOperationException("Only explicitly approved Microsoft-hosted images can be loaded inline.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("The image redirected to another URL; open its link in your browser instead.");
        response.EnsureSuccessStatusCode();
        var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
            throw new InvalidDataException("The source did not return a supported raster image.");
        if (response.Content.Headers.ContentLength > MaximumBytes)
            throw new InvalidDataException("The image is too large to show inline; open its link in a browser.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaximumBytes)
                throw new InvalidDataException("The image exceeded the inline size limit.");
            memory.Write(buffer, 0, read);
        }
        var data = $"data:{mime};base64,{Convert.ToBase64String(memory.ToArray())}";
        if (!AnswerMarkdownFormatter.IsSafeDataImage(data))
            throw new InvalidDataException("The downloaded content is not a valid raster image.");
        return data;
    }

    public void Dispose() => _client.Dispose();
}
