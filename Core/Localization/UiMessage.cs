using System.Text;
using System.Text.RegularExpressions;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Localization;

public sealed record UiMessage(string Key, params object?[] Arguments)
{
    public string Detail { get; init; } = "";
    public string? Parameter { get; init; }
    public string Resolve(UiText texts) => texts.Format(Key, Arguments) + Detail +
        (Parameter is null ? "" : " (" + texts.Format("DiagnosticParameter", Parameter) + ")");

    public static UiMessage? FromDiagnostic(string? message) =>
        string.IsNullOrWhiteSpace(message) ? null : DiagnosticCatalog.Parse(message);

    private static class DiagnosticCatalog
    {
        private sealed record Template(string Key, Regex Pattern, int ArgumentCount);
        private static readonly Lazy<Template[]> Templates = new(CreateTemplates);
        private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.CultureInvariant |
            RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

        public static UiMessage Parse(string message)
        {
            foreach (var template in Templates.Value)
            {
                var match = template.Pattern.Match(message);
                if (!match.Success) continue;
                var arguments = Enumerable.Range(0, template.ArgumentCount)
                    .Select(index => (object?)match.Groups[$"arg{index}"].Value).ToArray();
                return new UiMessage(template.Key, arguments)
                {
                    Detail = match.Groups["detail"].Value,
                    Parameter = match.Groups["parameter"].Success ? match.Groups["parameter"].Value : null
                };
            }
            return new UiMessage("DiagnosticUnknown", message);
        }

        private static Template[] CreateTemplates()
        {
            var templates = new List<(int Length, Template Template)>();
            var sources = new[] { ConversationLanguage.English, ConversationLanguage.Chinese }
                .SelectMany(language => UiText.For(language).Where(pair =>
                    pair.Key.StartsWith("Error", StringComparison.Ordinal))
                    .Select(pair => (pair.Key, Text: pair.Value)))
                .Concat(DiagnosticSources.Legacy);
            foreach (var (key, value) in sources)
            {
                var expression = new StringBuilder(@"\A");
                var offset = 0;
                var argumentCount = 0;
                foreach (Match placeholder in Placeholder.Matches(value))
                {
                    expression.Append(Regex.Escape(value[offset..placeholder.Index]));
                    var index = int.Parse(placeholder.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                    expression.Append($"(?<arg{index}>.*?)");
                    argumentCount = Math.Max(argumentCount, index + 1);
                    offset = placeholder.Index + placeholder.Length;
                }
                expression.Append(Regex.Escape(value[offset..]));
                // Preserve external diagnostic details, while localizing the application's explanation.
                expression.Append(@"(?:(?<detail>\r?\n.*)| \(Parameter '(?<parameter>[^']+)'\))?\z");
                templates.Add((value.Length, new Template(key, new Regex(expression.ToString(),
                    RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    TimeSpan.FromSeconds(1)), argumentCount)));
            }
            return templates.OrderByDescending(item => item.Length).Select(item => item.Template).ToArray();
        }
    }
}
