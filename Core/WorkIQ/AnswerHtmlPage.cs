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
                :root { color-scheme: light dark; --surface:#FFFFFF; --text:#242424;
                        --subtle:#F0F0F0; --stroke:#E0E0E0; --accent:#5B5FC7; }
                body { margin:0; padding:4px 8px 12px 0; background:var(--surface); color:var(--text);
                       font:14px/1.55 "Segoe UI Variable","Segoe UI",sans-serif; overflow-wrap:anywhere; }
                h1,h2,h3,h4 { line-height:1.3; margin:18px 0 8px; }
                h2 { font-size:16px; border-bottom:1px solid var(--stroke); padding-bottom:5px; }
                p,ul,ol,blockquote { margin:8px 0 14px; }
                ul,ol { padding-left:22px; }
                blockquote { border-left:3px solid var(--accent); padding:4px 12px; margin-left:0; }
                a { color:var(--accent); text-decoration:underline; }
                a:focus { outline:2px solid currentColor; outline-offset:2px; }
                table { display:block; max-width:100%; overflow-x:auto; border-collapse:collapse;
                        margin:12px 0; white-space:normal; }
                th,td { padding:6px 8px; border:1px solid var(--stroke); text-align:left; min-width:80px; }
                th { background:var(--subtle); font-weight:600; }
                tr:nth-child(even) { background:var(--subtle); }
                pre { overflow-x:auto; padding:10px; background:var(--subtle); border-radius:6px; }
                code { font-family:Consolas,monospace; }
                img { max-width:100%; max-height:360px; object-fit:contain; border-radius:8px;
                      display:block; margin:10px 0; }
                @media(prefers-color-scheme:dark) {
                  :root { --surface:#292929; --text:#F5F5F5; --subtle:#333333;
                          --stroke:#474747; --accent:#A6A7DC; }
                }
                @media(forced-colors:active) {
                  :root { --surface:Canvas; --text:CanvasText; --subtle:Canvas;
                          --stroke:CanvasText; --accent:LinkText; }
                }
              </style>
            </head>
            <body><main>{{answer.Html}}</main></body>
            </html>
            """;
    }
}
