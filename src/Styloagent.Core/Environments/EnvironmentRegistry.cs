using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Environments;

[YamlObject]
internal partial class EnvironmentPolicyFile
{
    public string ControlOwner { get; set; } = "overview-";
}

[YamlObject]
internal partial class EnvironmentTargetsFile
{
    public string? WebOrigin { get; set; }
    public string? ApiOrigin { get; set; }
    public string? SshHost { get; set; }
    public string? SshAccount { get; set; }
    public string? CredentialRef { get; set; }
    public string? BrowserCredentialRef { get; set; }
}

[YamlObject]
internal partial class EnvironmentCapacityFile
{
    public int? BrowserRead { get; set; }
    public int? BrowserWrite { get; set; }
    public int? Ssh { get; set; }
    public int? Deploy { get; set; }
}

[YamlObject]
internal partial class EnvironmentFile
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Color { get; set; } = "#718096";
    public string Icon { get; set; } = "cloud";
    public string Classification { get; set; } = "development";
    public string Owner { get; set; } = "";
    public string FallbackOwner { get; set; } = "overview-";
    public string Status { get; set; } = "available";
    public EnvironmentTargetsFile? Targets { get; set; }
    public EnvironmentCapacityFile? Capacity { get; set; }
}

/// <summary>Reads and creates environment definitions. Invalid entries are skipped and secrets are never resolved.</summary>
public static class EnvironmentRegistry
{
    public static string DefinitionsDir(string root) => Path.Combine(root, "definitions");
    public static string OwnershipDir(string root) => Path.Combine(root, "ownership");
    public static string PolicyFile(string root) => Path.Combine(root, "policy.yaml");

    public static string ReadControlOwner(string root)
    {
        try
        {
            if (!File.Exists(PolicyFile(root))) return "overview-";
            var file = YamlSerializer.Deserialize<EnvironmentPolicyFile>(File.ReadAllBytes(PolicyFile(root)));
            return NormalizeOwner(file?.ControlOwner, "overview-");
        }
        catch { return "overview-"; }
    }

    public static IReadOnlyList<EnvironmentDefinition> Read(string root)
    {
        var dir = DefinitionsDir(root);
        if (!Directory.Exists(dir)) return Array.Empty<EnvironmentDefinition>();
        var result = new List<EnvironmentDefinition>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    var f = YamlSerializer.Deserialize<EnvironmentFile>(File.ReadAllBytes(path));
                    if (f is null) continue;
                    var id = NormalizeId(string.IsNullOrWhiteSpace(f.Id) ? Path.GetFileNameWithoutExtension(path) : f.Id);
                    if (id is null) continue;
                    var targets = f.Targets ?? new EnvironmentTargetsFile();
                    var capacity = f.Capacity ?? new EnvironmentCapacityFile();
                    result.Add(new EnvironmentDefinition(
                        id,
                        string.IsNullOrWhiteSpace(f.DisplayName) ? id : f.DisplayName.Trim(),
                        f.Description?.Trim() ?? "",
                        string.IsNullOrWhiteSpace(f.Color) ? "#718096" : f.Color.Trim(),
                        string.IsNullOrWhiteSpace(f.Icon) ? "cloud" : f.Icon.Trim(),
                        string.IsNullOrWhiteSpace(f.Classification) ? "development" : f.Classification.Trim().ToLowerInvariant(),
                        NormalizeOwner(f.Owner, ReadControlOwner(root)),
                        NormalizeOwner(f.FallbackOwner, ReadControlOwner(root)),
                        string.IsNullOrWhiteSpace(f.Status) ? "available" : f.Status.Trim().ToLowerInvariant(),
                        new EnvironmentTargets(targets.WebOrigin, targets.ApiOrigin, targets.SshHost,
                            targets.SshAccount, targets.CredentialRef, targets.BrowserCredentialRef),
                        new EnvironmentCapacity(Positive(capacity.BrowserRead, 1), Positive(capacity.BrowserWrite, 1),
                            Positive(capacity.Ssh, 1), Positive(capacity.Deploy, 1))));
                }
                catch { /* one bad definition must not hide the others */ }
            }
        }
        catch { }
        return result;
    }

    /// <summary>Creates a minimal definition. Existing definitions are never overwritten.</summary>
    public static EnvironmentOperationResult Create(string root, string id, string displayName,
        string classification, string owner)
    {
        var normalized = NormalizeId(id);
        if (normalized is null) return EnvironmentOperationResult.Fail("invalid environment id");
        if (string.IsNullOrWhiteSpace(displayName)) return EnvironmentOperationResult.Fail("display name is required");
        try
        {
            var dir = DefinitionsDir(root);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, normalized + ".yaml");
            if (File.Exists(path)) return EnvironmentOperationResult.Fail($"environment '{normalized}' already exists");
            var file = new EnvironmentFile
            {
                Id = normalized,
                DisplayName = displayName.Trim(),
                Classification = string.IsNullOrWhiteSpace(classification) ? "development" : classification.Trim().ToLowerInvariant(),
                Owner = NormalizeOwner(owner, ReadControlOwner(root)),
                FallbackOwner = ReadControlOwner(root),
                Capacity = new EnvironmentCapacityFile { BrowserRead = 1, BrowserWrite = 1, Ssh = 1, Deploy = 1 },
            };
            File.WriteAllBytes(path, YamlSerializer.Serialize(file).ToArray());
            return EnvironmentOperationResult.Ok($"created environment '{normalized}' owned by {file.Owner}");
        }
        catch (Exception ex) { return EnvironmentOperationResult.Fail($"could not create environment: {ex.Message}"); }
    }

    /// <summary>Configures the non-secret browser target and capacity for an existing environment.</summary>
    public static EnvironmentOperationResult ConfigureBrowser(string root, string id, string webOrigin,
        string? browserCredentialRef, int readCapacity, int writeCapacity)
    {
        var normalized = NormalizeId(id);
        if (normalized is null) return EnvironmentOperationResult.Fail("invalid environment id");
        if (!Uri.TryCreate(webOrigin, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.Query.Length > 0 || origin.Fragment.Length > 0)
            return EnvironmentOperationResult.Fail("web_origin must be an http(s) origin without credentials, query, or fragment");
        if (!ValidCredentialReference(browserCredentialRef))
            return EnvironmentOperationResult.Fail("browser_credential_ref must be an opaque keychain://, infisical://, or secret:// reference");
        if (readCapacity is < 1 or > 32 || writeCapacity is < 1 or > 8)
            return EnvironmentOperationResult.Fail("browser capacity is outside the allowed range (read 1-32, write 1-8)");
        try
        {
            var path = Path.Combine(DefinitionsDir(root), normalized + ".yaml");
            if (!File.Exists(path)) return EnvironmentOperationResult.Fail($"unknown environment '{normalized}'");
            var file = YamlSerializer.Deserialize<EnvironmentFile>(File.ReadAllBytes(path));
            if (file is null) return EnvironmentOperationResult.Fail("environment definition is invalid");
            file.Targets ??= new EnvironmentTargetsFile();
            file.Targets.WebOrigin = origin.GetLeftPart(UriPartial.Authority);
            file.Targets.BrowserCredentialRef = string.IsNullOrWhiteSpace(browserCredentialRef) ? null : browserCredentialRef.Trim();
            file.Capacity ??= new EnvironmentCapacityFile();
            file.Capacity.BrowserRead = readCapacity;
            file.Capacity.BrowserWrite = writeCapacity;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, YamlSerializer.Serialize(file).ToArray());
            File.Move(temp, path, true);
            return EnvironmentOperationResult.Ok($"configured Playwright routing for '{normalized}' at {file.Targets.WebOrigin}");
        }
        catch (Exception ex) { return EnvironmentOperationResult.Fail($"could not configure browser routing: {ex.Message}"); }
    }

    internal static string NormalizeOwner(string? owner, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(owner) ? fallback : owner.Trim();
        return value.Length > 0 && value[^1] == '-' ? value : value + "-";
    }

    internal static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var value = id.Trim().ToLowerInvariant();
        return value.Length <= 64 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') ? value : null;
    }

    private static int Positive(int? value, int fallback) => value is > 0 ? value.Value : fallback;

    private static bool ValidCredentialReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme is "keychain" or "infisical" or "secret" &&
               string.IsNullOrEmpty(uri.UserInfo) && value.Length <= 256;
    }
}
