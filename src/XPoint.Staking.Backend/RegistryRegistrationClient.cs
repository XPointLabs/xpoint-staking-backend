using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using Microsoft.Extensions.Options;

namespace XPoint.Staking.Backend;

public sealed class RegistryRegistrationClient
{
    private readonly HttpClient _httpClient;
    private readonly BackendRegistryOptions _options;

    public RegistryRegistrationClient(HttpClient httpClient, IOptions<BackendRegistryOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<RegistrationDto>> GetRegistrationsAsync(
        string key,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return Array.Empty<RegistrationDto>();
        }

        var nodes = await _httpClient.GetFromJsonAsync<IReadOnlyList<RegistryNodeDto>>(
            "api/nodes",
            cancellationToken).ConfigureAwait(false);

        if (nodes is null || nodes.Count == 0)
        {
            return Array.Empty<RegistrationDto>();
        }

        var normalizedKey = NormalizeKey(key);
        var addressLookup = LooksLikeEthereumAddress(key);

        return nodes
            .Where(node => Matches(node, normalizedKey, addressLookup))
            .Select(ToRegistration)
            .Where(registration =>
                !string.IsNullOrWhiteSpace(registration.BlsPublicKey)
                && !string.IsNullOrWhiteSpace(registration.BlsSignature)
                && !string.IsNullOrWhiteSpace(registration.Ed25519PublicKey))
            .OrderBy(registration => registration.Ed25519PublicKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<RegistrySigningNode>> GetSigningNodesAsync(
        CancellationToken cancellationToken)
    {
        var nodes = await GetRegistryNodesAsync(cancellationToken).ConfigureAwait(false);
        return nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.SigningEndpoint))
            .OrderBy(node => node.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<RegistrySigningNode>> GetRegistryNodesAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return Array.Empty<RegistrySigningNode>();
        }

        var nodes = await _httpClient.GetFromJsonAsync<IReadOnlyList<RegistryNodeDto>>(
            "api/nodes",
            cancellationToken).ConfigureAwait(false);

        if (nodes is null || nodes.Count == 0)
        {
            return Array.Empty<RegistrySigningNode>();
        }

        return nodes
            .Select(ToSigningNode)
            .Where(node =>
                !string.IsNullOrWhiteSpace(node.BlsPublicKey))
            .OrderBy(node => node.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Matches(RegistryNodeDto node, string normalizedKey, bool addressLookup)
    {
        if (addressLookup)
        {
            return string.Equals(NormalizeKey(node.OperatorAddress), normalizedKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeKey(node.RewardsAddress), normalizedKey, StringComparison.OrdinalIgnoreCase)
                || node.Contributors.Any(contributor =>
                    string.Equals(NormalizeKey(contributor.Address), normalizedKey, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(NormalizeKey(contributor.Beneficiary), normalizedKey, StringComparison.OrdinalIgnoreCase));
        }

        var hexKey = NormalizeHexKey(normalizedKey);
        return string.Equals(NormalizeHexKey(node.NodeId), hexKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeHexKey(node.Ed25519PublicKey), hexKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeHexKey(node.BlsPublicKey.Data), hexKey, StringComparison.OrdinalIgnoreCase);
    }

    private static RegistrationDto ToRegistration(RegistryNodeDto node)
    {
        var blsPublicKey = NormalizeBlsPublicKey(node.BlsPublicKey);
        var edPublicKey = NormalizeFixedHex(
            string.IsNullOrWhiteSpace(node.Ed25519PublicKey) ? node.NodeId : node.Ed25519PublicKey,
            32);
        var blsSignature = NormalizeFixedHex(node.BlsSignature, 256);
        var edSignature = NormalizeEd25519Signature(node);

        return new RegistrationDto
        {
            Operator = node.OperatorAddress,
            BlsPublicKey = blsPublicKey,
            Ed25519PublicKey = edPublicKey,
            BlsSignature = blsSignature,
            Ed25519Signature = edSignature,
            Timestamp = node.UpdatedAt.ToUnixTimeMilliseconds() / 1000.0
        };
    }

    private static RegistrySigningNode ToSigningNode(RegistryNodeDto node)
    {
        var blsPublicKey = NormalizeBlsPublicKey(node.BlsPublicKey);
        var nodeId = NormalizeFixedHex(
            string.IsNullOrWhiteSpace(node.Ed25519PublicKey) ? node.NodeId : node.Ed25519PublicKey,
            32);

        return new RegistrySigningNode(
            nodeId,
            blsPublicKey,
            NormalizeSigningEndpoint(node.SigningEndpoint),
            node.UpdatedAt,
            node.TransportStatus is null ? null : ToTransportStatus(node.TransportStatus),
            node.TransportHealthySince,
            node.TransportUnhealthySince);
    }

    private static RegistryTransportStatus ToTransportStatus(RegistryTransportStatusDto status)
    {
        return new RegistryTransportStatus
        {
            Enabled = status.Enabled,
            Running = status.Running,
            Degraded = status.Degraded,
            Mode = status.Mode,
            Mocked = status.Mocked,
            RestartCount = status.RestartCount,
            ConsecutiveFailures = status.ConsecutiveFailures,
            LastExitReason = status.LastExitReason,
            LastStartedAt = status.LastStartedAt,
            DegradedUntil = status.DegradedUntil
        };
    }

    private static string NormalizeBlsPublicKey(RegistryBlsPublicKeyDto key)
    {
        var data = NormalizeFixedHex(key.Data, 128);
        if (!string.IsNullOrWhiteSpace(data))
        {
            return data;
        }

        var x = FormatUint256Hex(key.X);
        var y = FormatUint256Hex(key.Y);
        return string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y)
            ? ""
            : x.PadLeft(128, '0') + y.PadLeft(128, '0');
    }

    private static string NormalizeEd25519Signature(RegistryNodeDto node)
    {
        var first = NormalizeFixedHex(node.Ed25519Signature1, 32);
        var second = NormalizeFixedHex(node.Ed25519Signature2, 32);
        return string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)
            ? ""
            : first + second;
    }

    private static string NormalizeSigningEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "";
        }

        var trimmed = endpoint.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? trimmed
            : "";
    }

    private static string NormalizeFixedHex(string? value, int expectedBytes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = Strip0x(value.Trim()).ToLowerInvariant();
        return normalized.Length == expectedBytes * 2 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : "";
    }

    private static string FormatUint256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = trimmed[2..];
            return hex.Length is > 0 and <= 64 && hex.All(Uri.IsHexDigit)
                ? hex.PadLeft(64, '0').ToLowerInvariant()
                : "";
        }

        return BigInteger.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= BigInteger.Zero
            && number < (BigInteger.One << 256)
            ? number.ToString("x", CultureInfo.InvariantCulture).PadLeft(64, '0').ToLowerInvariant()
            : "";
    }

    private static bool LooksLikeEthereumAddress(string value)
    {
        var normalized = Strip0x(value.Trim());
        return normalized.Length == 40 && normalized.All(Uri.IsHexDigit);
    }

    private static string NormalizeKey(string value) => value.Trim().ToLowerInvariant();

    private static string NormalizeHexKey(string value) => Strip0x(value.Trim()).ToLowerInvariant().TrimStart('0').PadLeft(1, '0');

    private static string Strip0x(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

    private sealed record RegistryNodeDto
    {
        public string NodeId { get; init; } = "";
        public string OperatorAddress { get; init; } = "";
        public string RewardsAddress { get; init; } = "";
        public RegistryBlsPublicKeyDto BlsPublicKey { get; init; } = new();
        public string BlsSignature { get; init; } = "";
        public string Ed25519PublicKey { get; init; } = "";
        public string Ed25519Signature1 { get; init; } = "";
        public string Ed25519Signature2 { get; init; } = "";
        public string SigningEndpoint { get; init; } = "";
        public IReadOnlyList<RegistryContributorDto> Contributors { get; init; } = Array.Empty<RegistryContributorDto>();
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
        public RegistryTransportStatusDto? TransportStatus { get; init; }
        public DateTimeOffset? TransportHealthySince { get; init; }
        public DateTimeOffset? TransportUnhealthySince { get; init; }
    }

    private sealed record RegistryBlsPublicKeyDto
    {
        public string Data { get; init; } = "";
        public string X { get; init; } = "";
        public string Y { get; init; } = "";
    }

    private sealed record RegistryContributorDto
    {
        public string Address { get; init; } = "";
        public string Beneficiary { get; init; } = "";
    }

    private sealed record RegistryTransportStatusDto
    {
        public bool Enabled { get; init; }
        public bool Running { get; init; }
        public bool Degraded { get; init; }
        public string Mode { get; init; } = "";
        public bool Mocked { get; init; }
        public int RestartCount { get; init; }
        public int ConsecutiveFailures { get; init; }
        public string? LastExitReason { get; init; }
        public DateTimeOffset? LastStartedAt { get; init; }
        public DateTimeOffset? DegradedUntil { get; init; }
    }
}
