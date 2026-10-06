using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using Ganss.Xss;
using Markdig;

namespace ConversationAssistant.Core.WorkIQ;

public sealed record RenderedAnswer(string Html, IReadOnlyDictionary<string, string> RemoteImages);

public sealed class AnswerMarkdownFormatter
{
    private const int MaximumAnswerCharacters = 1_000_000;
    private const int MaximumEmbeddedImageBytes = 512_000;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] JpegSignature = [255, 216, 255];
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().Build();

    public RenderedAnswer Format(string markdown,
        IReadOnlyDictionary<string, string>? approvedImages = null,
        ConversationLanguage language = ConversationLanguage.English)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var texts = UiText.For(language);
        if (markdown.Length > MaximumAnswerCharacters)
            throw new ArgumentOutOfRangeException(nameof(markdown), texts["ErrorRenderLimit"]);

        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "h1", "h2", "h3", "h4", "strong", "em", "del",
            "ul", "ol", "li", "blockquote", "pre", "code", "br", "hr", "table", "thead",
            "tbody", "tr", "th", "td", "a", "img", "span", "div" })
            sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "href", "src", "alt", "title", "colspan", "rowspan" })
            sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "https", "mailto", "data" })
            sanitizer.AllowedSchemes.Add(scheme);
        sanitizer.AllowedCssProperties.Clear();

        var sanitized = sanitizer.Sanitize(Markdown.ToHtml(markdown, Pipeline));
        var document = new HtmlParser().ParseDocument($"<body>{sanitized}</body>");
        var remoteImages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var image in document.QuerySelectorAll("img").ToArray())
        {
            var source = image.GetAttribute("src") ?? "";
            var alt = image.GetAttribute("alt") ?? texts["Image"];
            if (IsSafeDataImage(source)) continue;
            if (IsSafeRemoteUri(source, allowMail: false, out var approvedUri) &&
                approvedImages?.TryGetValue(approvedUri!.AbsoluteUri, out var data) == true &&
                IsSafeDataImage(data))
            {
                image.SetAttribute("src", data);
                remoteImages[approvedUri.AbsoluteUri] = alt;
                continue;
            }
            var replacement = document.CreateElement("span");
            if (IsSafeRemoteUri(source, allowMail: false, out var uri))
            {
                replacement.TextContent = texts.Format("ImagePrefix", alt);
                var link = document.CreateElement("a");
                link.SetAttribute("href", uri!.AbsoluteUri);
                link.TextContent = texts.Format("ImageOpenHost", uri.Host);
                replacement.AppendChild(link);
                remoteImages[uri.AbsoluteUri] = alt;
            }
            else
            {
                replacement.TextContent = texts.Format("ImageUnavailable", alt);
            }
            image.Parent?.ReplaceChild(replacement, image);
        }

        foreach (var link in document.QuerySelectorAll("a"))
        {
            if (!IsSafeRemoteUri(link.GetAttribute("href") ?? "", allowMail: true, out var uri))
                link.RemoveAttribute("href");
            else
                link.SetAttribute("href", uri!.AbsoluteUri);
        }
        return new RenderedAnswer(document.Body?.InnerHtml ?? "", remoteImages);
    }

    public static bool IsSafeRemoteUri(string value, bool allowMail, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps && (!allowMail || parsed.Scheme != Uri.UriSchemeMailto))
            return false;
        if (parsed.Scheme == Uri.UriSchemeHttps && (string.IsNullOrWhiteSpace(parsed.Host) ||
            parsed.IsLoopback || System.Net.IPAddress.TryParse(parsed.Host, out _))) return false;
        uri = parsed;
        return true;
    }

    public static bool CanFetchInlineImage(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri) return false;
        if (!IsSafeRemoteUri(uri.AbsoluteUri, allowMail: false, out _)) return false;
        var host = uri.IdnHost;
        return host.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".sharepoint-df.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".onedrive.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".office.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeDataImage(string value)
    {
        var separator = value.IndexOf(',');
        if (separator < 0 || separator > 35) return false;
        var mime = value[..separator].ToLowerInvariant();
        if (mime is not ("data:image/png;base64" or "data:image/jpeg;base64" or
            "data:image/gif;base64" or "data:image/webp;base64") ||
            value.Length - separator - 1 > (MaximumEmbeddedImageBytes + 2) / 3 * 4)
            return false;
        try
        {
            var bytes = Convert.FromBase64String(value[(separator + 1)..]);
            if (bytes.Length > MaximumEmbeddedImageBytes) return false;
            return mime switch
            {
                "data:image/png;base64" => bytes.AsSpan().StartsWith(PngSignature),
                "data:image/jpeg;base64" => bytes.AsSpan().StartsWith(JpegSignature),
                "data:image/gif;base64" => bytes.AsSpan().StartsWith("GIF8"u8),
                _ => bytes.AsSpan().StartsWith("RIFF"u8) && bytes.Length >= 12 &&
                     bytes.AsSpan(8).StartsWith("WEBP"u8)
            };
        }
        catch (FormatException) { return false; }
    }
}
