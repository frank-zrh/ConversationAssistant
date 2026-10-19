using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ConversationAssistant.Core.Models;

public sealed record ProductGuide
{
    public const int MaximumNameLength = 200;
    public const int MaximumTextLength = 8000;
    public const int MaximumMetadataItems = 20;

    public required string Name { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public required string Overview { get; init; }
    public required string LicensingAndPricing { get; init; }
    public required string SuitableScenarios { get; init; }
    public required string Limitations { get; init; }
    public string AdditionalNotes { get; init; } = "";
    public IReadOnlyList<string> Sources { get; init; } = [];

    [JsonIgnore]
    public IEnumerable<string> IdentityNames => Aliases.Prepend(Name);

    public static string IdentityKey(string name) =>
        Regex.Replace(name.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();

    public ProductGuide Snapshot() => this with { Aliases = Aliases.ToArray(), Sources = Sources.ToArray() };
}
