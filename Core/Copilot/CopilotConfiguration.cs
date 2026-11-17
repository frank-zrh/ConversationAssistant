namespace ConversationAssistant.Core.Copilot;

public sealed class CopilotConfiguration
{
    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public bool PreviewConsent { get; init; }

    public void Validate()
    {
        if (!Guid.TryParse(TenantId, out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParse(ClientId, out var client) || client == Guid.Empty)
            throw new InvalidOperationException("Copilot requires its own organization Tenant ID and public-client application Client ID (GUIDs). Speech credentials cannot be reused.");
        if (!PreviewConsent)
            throw new InvalidOperationException("Acknowledge the Copilot Chat preview and workplace-data submission notice in Settings before signing in.");
    }

    public bool HasSameIdentity(CopilotConfiguration other) =>
        string.Equals(TenantId, other.TenantId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ClientId, other.ClientId, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => "Copilot configuration (identity redacted)";
}
