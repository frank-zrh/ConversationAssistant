using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.WorkIQ;

public static class AnswerHtmlPage
{
    public static string Build(RenderedAnswer answer, long revision = 0,
        ConversationLanguage language = ConversationLanguage.English)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return $$"""
            <!doctype html>
            <html lang="{{UiText.For(language).LanguageTag}}">
            <head>
              <meta charset="utf-8">
              <meta name="mc-render-id" content="{{revision}}">
              <meta http-equiv="Content-Security-Policy"
                    content="default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src data:; media-src 'none'; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <style>
                :root { color-scheme: light dark; }
                body { margin:0; padding:4px 8px 16px 0; background:#fafafa; color:#1f2937;
                       font:14px/1.6 "Segoe UI",sans-serif; overflow-wrap:anywhere; }
                h1,h2,h3,h4 { line-height:1.3; margin:18px 0 8px; }
                h2 { font-size:16px; border-bottom:1px solid #d1d5db; padding-bottom:5px; }
                p,ul,ol,blockquote { margin:8px 0 14px; }
                ul,ol { padding-left:22px; }
                blockquote { border-left:3px solid #879db7; padding:4px 12px; margin-left:0; }
                a { color:#155ab7; text-decoration:underline; }
                a:focus { outline:2px solid #155ab7; }
                table { display:block; max-width:100%; overflow-x:auto; border-collapse:collapse;
                        margin:12px 0; white-space:normal; }
                th,td { padding:7px 10px; border:1px solid #cbd5e1; text-align:left; min-width:80px; }
                th { background:#e9eef5; font-weight:600; }
                tr:nth-child(even) { background:#f3f6fb; }
                pre { overflow-x:auto; padding:12px; background:#e9eef5; border-radius:6px; }
                code { font-family:Consolas,monospace; }
                img { max-width:100%; max-height:360px; object-fit:contain; border-radius:8px;
                      display:block; margin:10px 0; }
                @media(prefers-color-scheme:dark) {
                  body { background:#1f2228; color:#edf0f4; }
                  a { color:#88b7ff; }
                  h2,th,td { border-color:#48515e; }
                  th,pre { background:#333b45; }
                  tr:nth-child(even) { background:#292f37; }
                }
              </style>
            </head>
            <body><main>{{answer.Html}}</main></body>
            </html>
            """;
    }
}
