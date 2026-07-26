using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace XPoint.Staking.Backend;

/// <summary>Loads the immutable deployment record before service registration.</summary>
public sealed record DeploymentManifestBinding(
    long ChainId,
    string Network,
    string TokenAddress,
    string ServiceNodeRewardsAddress,
    string ServiceNodeContributionFactoryAddress,
    string RewardRatePoolAddress,
    long StakingRequirementAtomic,
    int MaxContributors,
    string? LifecycleId)
{
    public const int SchemaVersion = 1;

    public static DeploymentManifestBinding? Load(IConfiguration configuration)
    {
        var path = configuration["Contracts:DeploymentManifestPath"];
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new MemoryStream();
            stream.CopyTo(output);
            bytes = output.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Contracts:DeploymentManifestPath cannot be read.", ex);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            RequireSchemaVersion(root);
            var binding = new DeploymentManifestBinding(
                RequireLong(root, "chainId"),
                RequireString(root, "network"),
                RequireAddress(RequireObject(root, "contracts"), "token"),
                RequireAddress(RequireObject(root, "contracts"), "serviceNodeRewards"),
                RequireAddress(RequireObject(root, "contracts"), "serviceNodeContributionFactory"),
                RequireAddress(RequireObject(root, "contracts"), "rewardRatePool"),
                RequireLong(RequireObject(root, "parameters"), "stakingRequirement"),
                checked((int)RequireLong(RequireObject(root, "parameters"), "maxContributors")),
                GetLifecycleId(root));

            if (binding.ChainId <= 0 || binding.StakingRequirementAtomic <= 0 || binding.MaxContributors <= 0)
            {
                throw new InvalidOperationException("Deployment manifest contains non-positive chain parameters.");
            }

            if (binding.LifecycleId is not null && (binding.LifecycleId.Length != 64 || !binding.LifecycleId.All(Uri.IsHexDigit)))
            {
                throw new InvalidOperationException("Deployment manifest lifecycleId must be a 32-byte hexadecimal value.");
            }

            binding.ValidateConfiguredValues(configuration);
            return binding;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Contracts:DeploymentManifestPath is not valid JSON.", ex);
        }
    }

    public IReadOnlyDictionary<string, string?> ToConfigurationOverrides() => new Dictionary<string, string?>
    {
        ["Contracts:ChainId"] = ChainId.ToString(CultureInfo.InvariantCulture),
        ["Contracts:NetworkName"] = Network,
        ["Contracts:TokenAddress"] = TokenAddress,
        ["Contracts:ServiceNodeRewardsAddress"] = ServiceNodeRewardsAddress,
        ["Contracts:ServiceNodeContributionFactoryAddress"] = ServiceNodeContributionFactoryAddress,
        ["Contracts:RewardRatePoolAddress"] = RewardRatePoolAddress,
        ["Contracts:StakingRequirementAtomic"] = StakingRequirementAtomic.ToString(CultureInfo.InvariantCulture),
        ["Contracts:MaxStakers"] = MaxContributors.ToString(CultureInfo.InvariantCulture)
        , ["Contracts:DeploymentLifecycleId"] = LifecycleId
    };

    private void ValidateConfiguredValues(IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["Contracts:ExpectedDeploymentNetwork"]))
        {
            throw new InvalidOperationException("Contracts:ExpectedDeploymentNetwork is required when Contracts:DeploymentManifestPath is configured.");
        }
        MatchString(configuration, "Contracts:ExpectedDeploymentNetwork", Network);
        MatchLong(configuration, "Contracts:ChainId", ChainId);
        MatchString(configuration, "Contracts:NetworkName", Network);
        MatchAddress(configuration, "Contracts:TokenAddress", TokenAddress);
        MatchAddress(configuration, "Contracts:ServiceNodeRewardsAddress", ServiceNodeRewardsAddress);
        MatchAddress(configuration, "Contracts:ServiceNodeContributionFactoryAddress", ServiceNodeContributionFactoryAddress);
        MatchAddress(configuration, "Contracts:RewardRatePoolAddress", RewardRatePoolAddress);
        MatchLong(configuration, "Contracts:StakingRequirementAtomic", StakingRequirementAtomic);
        MatchLong(configuration, "Contracts:MaxStakers", MaxContributors);
    }

    private static void RequireSchemaVersion(JsonElement root)
    {
        if (!root.TryGetProperty("schemaVersion", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var version)
            || version != SchemaVersion)
        {
            throw new InvalidOperationException("Deployment manifest schemaVersion is not supported.");
        }
    }

    private static void MatchLong(IConfiguration configuration, string key, long expected)
    {
        var configured = configuration[key];
        if (!string.IsNullOrWhiteSpace(configured)
            && (!long.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actual) || actual != expected))
        {
            throw new InvalidOperationException($"{key} conflicts with Contracts:DeploymentManifestPath.");
        }
    }

    private static void MatchString(IConfiguration configuration, string key, string expected)
    {
        var configured = configuration[key];
        if (!string.IsNullOrWhiteSpace(configured) && !string.Equals(configured.Trim(), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{key} conflicts with Contracts:DeploymentManifestPath.");
        }
    }

    private static void MatchAddress(IConfiguration configuration, string key, string expected)
    {
        var configured = configuration[key];
        if (!string.IsNullOrWhiteSpace(configured)
            && !string.Equals(EthereumJsonRpcClient.NormalizeAddress(configured), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{key} conflicts with Contracts:DeploymentManifestPath.");
        }
    }

    private static JsonElement RequireObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : throw new InvalidOperationException($"Deployment manifest requires object '{name}'.");

    private static string RequireString(JsonElement parent, string name, string? expected = null)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"Deployment manifest requires string '{name}'.");
        }
        var result = value.GetString()!.Trim();
        if (expected is not null && !string.Equals(result, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Deployment manifest schema is not supported.");
        }
        return result;
    }

    private static string RequireAddress(JsonElement parent, string name) => EthereumJsonRpcClient.NormalizeAddress(RequireString(parent, name));

    private static long RequireLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || !(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
                 || value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)))
        {
            throw new InvalidOperationException($"Deployment manifest requires integer '{name}'.");
        }
        return result;
    }

    private static string? GetLifecycleId(JsonElement root)
    {
        if (root.TryGetProperty("lifecycleId", out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString()?.Trim();
        }
        return root.TryGetProperty("lifecycle", out var lifecycle)
            && lifecycle.ValueKind == JsonValueKind.Object
            && lifecycle.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String ? id.GetString()?.Trim() : null;
    }
}
