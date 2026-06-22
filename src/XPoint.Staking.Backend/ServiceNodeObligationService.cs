using Microsoft.Extensions.Options;

namespace XPoint.Staking.Backend;

public sealed class ServiceNodeObligationService
{
    private readonly EventIndexer _indexer;
    private readonly RegistryRegistrationClient _registry;
    private readonly IOptions<BackendRegistryOptions> _options;
    private readonly ILogger<ServiceNodeObligationService> _logger;

    public ServiceNodeObligationService(
        EventIndexer indexer,
        RegistryRegistrationClient registry,
        IOptions<BackendRegistryOptions> options,
        ILogger<ServiceNodeObligationService> logger)
    {
        _indexer = indexer;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ServiceNodeObligationStatus>> GetStatusesAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RegistrySigningNode> registryNodes;
        try
        {
            registryNodes = await _registry.GetRegistryNodesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read registry nodes while building service node obligations.");
            registryNodes = Array.Empty<RegistrySigningNode>();
        }

        var registryByBls = registryNodes
            .GroupBy(node => NormalizeBlsPublicKey(node.BlsPublicKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var options = _options.Value;
        var now = DateTimeOffset.UtcNow;

        return _indexer.GetContractNodes()
            .Where(static item => item.Value.In && !string.IsNullOrWhiteSpace(item.Value.BlsPublicKey))
            .OrderBy(static item => item.Key)
            .Select(item =>
            {
                var blsPublicKey = NormalizeBlsPublicKey(item.Value.BlsPublicKey);
                registryByBls.TryGetValue(blsPublicKey, out var registryNode);
                var projectedNode = _indexer.GetNode(item.Key.ToString());
                return BuildStatus(item.Key, item.Value, projectedNode, registryNode, options, now);
            })
            .ToArray();
    }

    public async Task<ServiceNodeObligationStatus> GetStatusByBlsPublicKeyAsync(
        string blsPublicKey,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeBlsPublicKey(blsPublicKey);
        var statuses = await GetStatusesAsync(cancellationToken).ConfigureAwait(false);
        return statuses.FirstOrDefault(status =>
            string.Equals(status.BlsPublicKey, normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("BLS public key is not active on the service node rewards contract.");
    }

    public async Task<IReadOnlySet<string>> GetRewardEligibleNodeIdsAsync(CancellationToken cancellationToken)
    {
        var statuses = await GetStatusesAsync(cancellationToken).ConfigureAwait(false);
        return statuses
            .Where(static status => status.RewardEligible)
            .Select(static status => status.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static ServiceNodeObligationStatus BuildStatus(
        long contractId,
        ContractNodeStatusDto contractNode,
        ProjectedNode? projectedNode,
        RegistrySigningNode? registryNode,
        BackendRegistryOptions options,
        DateTimeOffset now)
    {
        var chainStatus = projectedNode?.Status ?? (contractNode.In ? "active" : "inactive");
        var nodeId = projectedNode?.NodeId ?? contractId.ToString();
        var ed25519PublicKey = string.IsNullOrWhiteSpace(contractNode.Ed25519PublicKey)
            ? projectedNode?.ServiceNodePubkey ?? ""
            : contractNode.Ed25519PublicKey;
        var exitEligible = string.Equals(chainStatus, "exiting", StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(chainStatus, "active", StringComparison.OrdinalIgnoreCase)
            && !exitEligible)
        {
            return CreateBase(
                nodeId,
                contractId,
                contractNode.BlsPublicKey,
                ed25519PublicKey,
                chainStatus,
                "inactive",
                "Node is not active on the rewards contract.",
                registryNode,
                now,
                rewardEligible: false,
                exitEligible: false,
                liquidationEligible: false);
        }

        if (exitEligible)
        {
            return CreateBase(
                nodeId,
                contractId,
                contractNode.BlsPublicKey,
                ed25519PublicKey,
                chainStatus,
                "exit-requested",
                "ServiceNodeExitRequest is indexed for this node.",
                registryNode,
                now,
                rewardEligible: false,
                exitEligible: true,
                liquidationEligible: false);
        }

        if (registryNode is null)
        {
            return CreateBase(
                nodeId,
                contractId,
                contractNode.BlsPublicKey,
                ed25519PublicKey,
                chainStatus,
                "registry-missing",
                "Active node is missing from the registry heartbeat set.",
                null,
                now,
                rewardEligible: false,
                exitEligible: false,
                liquidationEligible: false);
        }

        var heartbeatAge = now - registryNode.UpdatedAt;
        var heartbeatGrace = TimeSpan.FromSeconds(Math.Max(1, options.HeartbeatGraceSeconds));
        var decommissionGrace = TimeSpan.FromSeconds(Math.Max(options.HeartbeatGraceSeconds + 1, options.DecommissionGraceSeconds));
        var liquidationGrace = TimeSpan.FromSeconds(Math.Max(options.DecommissionGraceSeconds + 1, options.LiquidationGraceSeconds));

        if (heartbeatAge >= liquidationGrace)
        {
            return CreateTimed(
                nodeId,
                contractId,
                contractNode,
                ed25519PublicKey,
                chainStatus,
                "liquidatable",
                "Registry heartbeat has been absent past the liquidation grace period.",
                registryNode,
                now,
                registryNode.UpdatedAt,
                decommissionGrace,
                liquidationGrace,
                rewardEligible: false,
                liquidationEligible: true);
        }

        if (heartbeatAge >= decommissionGrace)
        {
            return CreateTimed(
                nodeId,
                contractId,
                contractNode,
                ed25519PublicKey,
                chainStatus,
                "decommissioned",
                "Registry heartbeat is stale past the decommission grace period.",
                registryNode,
                now,
                registryNode.UpdatedAt,
                decommissionGrace,
                liquidationGrace,
                rewardEligible: false,
                liquidationEligible: false);
        }

        if (heartbeatAge > heartbeatGrace)
        {
            return CreateTimed(
                nodeId,
                contractId,
                contractNode,
                ed25519PublicKey,
                chainStatus,
                "heartbeat-stale",
                "Registry heartbeat is stale.",
                registryNode,
                now,
                registryNode.UpdatedAt,
                decommissionGrace,
                liquidationGrace,
                rewardEligible: false,
                liquidationEligible: false);
        }

        if (!IsTransportHealthy(registryNode.TransportStatus))
        {
            var unhealthySince = registryNode.TransportUnhealthySince ?? registryNode.UpdatedAt;
            var unhealthyAge = now - unhealthySince;
            if (unhealthyAge >= liquidationGrace)
            {
                return CreateTimed(
                    nodeId,
                    contractId,
                    contractNode,
                    ed25519PublicKey,
                    chainStatus,
                    "liquidatable",
                    "Transport has been unhealthy past the liquidation grace period.",
                    registryNode,
                    now,
                    unhealthySince,
                    decommissionGrace,
                    liquidationGrace,
                    rewardEligible: false,
                    liquidationEligible: true);
            }

            if (unhealthyAge >= decommissionGrace)
            {
                return CreateTimed(
                    nodeId,
                    contractId,
                    contractNode,
                    ed25519PublicKey,
                    chainStatus,
                    "decommissioned",
                    "Transport has been unhealthy past the decommission grace period.",
                    registryNode,
                    now,
                    unhealthySince,
                    decommissionGrace,
                    liquidationGrace,
                    rewardEligible: false,
                    liquidationEligible: false);
            }

            return CreateTimed(
                nodeId,
                contractId,
                contractNode,
                ed25519PublicKey,
                chainStatus,
                registryNode.TransportStatus is null ? "transport-status-missing" : "transport-unhealthy",
                registryNode.TransportStatus is null
                    ? "Registry heartbeat does not include transport runtime status."
                    : "Transport runtime is not healthy.",
                registryNode,
                now,
                unhealthySince,
                decommissionGrace,
                liquidationGrace,
                rewardEligible: false,
                liquidationEligible: false);
        }

        return CreateBase(
            nodeId,
            contractId,
            contractNode.BlsPublicKey,
            ed25519PublicKey,
            chainStatus,
            "active",
            "Node heartbeat and transport are healthy.",
            registryNode,
            now,
            rewardEligible: true,
            exitEligible: false,
            liquidationEligible: false);
    }

    private static ServiceNodeObligationStatus CreateTimed(
        string nodeId,
        long contractId,
        ContractNodeStatusDto contractNode,
        string ed25519PublicKey,
        string chainStatus,
        string status,
        string reason,
        RegistrySigningNode registryNode,
        DateTimeOffset now,
        DateTimeOffset issueSince,
        TimeSpan decommissionGrace,
        TimeSpan liquidationGrace,
        bool rewardEligible,
        bool liquidationEligible)
    {
        var result = CreateBase(
            nodeId,
            contractId,
            contractNode.BlsPublicKey,
            ed25519PublicKey,
            chainStatus,
            status,
            reason,
            registryNode,
            now,
            rewardEligible,
            exitEligible: false,
            liquidationEligible: liquidationEligible);
        return result with
        {
            TransportUnhealthySince = issueSince,
            TransportUnhealthySeconds = Math.Max(0, (long)(now - issueSince).TotalSeconds),
            DecommissionEligibleAtUnix = issueSince.Add(decommissionGrace).ToUnixTimeSeconds(),
            LiquidationEligibleAtUnix = issueSince.Add(liquidationGrace).ToUnixTimeSeconds()
        };
    }

    private static ServiceNodeObligationStatus CreateBase(
        string nodeId,
        long contractId,
        string blsPublicKey,
        string ed25519PublicKey,
        string chainStatus,
        string status,
        string reason,
        RegistrySigningNode? registryNode,
        DateTimeOffset now,
        bool rewardEligible,
        bool exitEligible,
        bool liquidationEligible)
    {
        return new ServiceNodeObligationStatus
        {
            NodeId = nodeId,
            ContractId = contractId,
            BlsPublicKey = NormalizeBlsPublicKey(blsPublicKey),
            Ed25519PublicKey = ed25519PublicKey,
            ChainStatus = chainStatus,
            Status = status,
            Reason = reason,
            LastHeartbeatAt = registryNode?.UpdatedAt,
            HeartbeatAgeSeconds = registryNode is null ? null : Math.Max(0, (long)(now - registryNode.UpdatedAt).TotalSeconds),
            TransportUnhealthySince = registryNode?.TransportUnhealthySince,
            TransportUnhealthySeconds = registryNode?.TransportUnhealthySince is null
                ? null
                : Math.Max(0, (long)(now - registryNode.TransportUnhealthySince.Value).TotalSeconds),
            RewardEligible = rewardEligible,
            ExitSignatureEligible = exitEligible,
            LiquidationSignatureEligible = liquidationEligible
        };
    }

    private static bool IsTransportHealthy(RegistryTransportStatus? status)
    {
        return status is { Enabled: true, Running: true, Degraded: false, Mocked: false };
    }

    private static string NormalizeBlsPublicKey(string value)
    {
        var normalized = EthereumJsonRpcClient.Strip0x(value.Trim()).ToLowerInvariant();
        if (normalized.Length != 256 || !normalized.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Expected 128-byte BLS public key hex value.");
        }

        return normalized;
    }
}
