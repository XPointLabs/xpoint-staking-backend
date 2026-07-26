using System.Text.Json;

namespace XPoint.Staking.Backend;

public sealed record ChainEvent
{
    public long ChainId { get; init; } = 42_161;
    public long BlockNumber { get; init; }
    public long? BlockTimestamp { get; init; }
    public string BlockHash { get; init; } = "";
    public string TransactionHash { get; init; } = "";
    public int LogIndex { get; init; }
    public string Address { get; init; } = "";
    public string? MainArg { get; init; }
    public string Name { get; init; } = "";
    public Dictionary<string, JsonElement> Args { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record IndexedEvent(
    string Id,
    ChainEvent Event,
    DateTimeOffset IndexedAt);

public sealed record ChainTipRequest
{
    public long BlockNumber { get; init; }
    public string BlockHash { get; init; } = "";
}

public sealed record IngestEventResult(
    string Id,
    bool Inserted,
    long TotalEvents);

public sealed record EventIngestionStats(
    long Attempted,
    long Inserted,
    long Duplicates,
    long StaleNodeProjectionIgnored,
    long StaleStatusIgnored,
    long CorruptedStateRecoveries,
    long StaleStateQuarantines,
    long StatePersistenceFailures,
    long TotalEvents);

public sealed record ProjectedNode
{
    public string NodeId { get; init; } = "";
    public string ServiceNodePubkey { get; init; } = "";
    public string OperatorAddress { get; init; } = "";
    public string RewardsAddress { get; init; } = "";
    public string BlsPublicKeyData { get; init; } = "";
    public string BlsPublicKeyX { get; init; } = "";
    public string BlsPublicKeyY { get; init; } = "";
    public int OperatorFeeBps { get; init; }
    public long StakeAtomic { get; init; }
    public IReadOnlyList<ProjectedContributor> Contributors { get; init; } = Array.Empty<ProjectedContributor>();
    public string Status { get; init; } = "active";
    public long RegistrationBlockNumber { get; init; }
    public long? ExitRequestedBlockNumber { get; init; }
    public long LastBlockNumber { get; init; }
}

public sealed record ProjectedContributor
{
    public string Address { get; init; } = "";
    public string Beneficiary { get; init; } = "";
    public long AmountAtomic { get; init; }
}

public sealed record RewardState(
    string Address,
    string TokenSymbol,
    int TokenDecimals,
    long LifetimeRewardsAtomic,
    long ClaimedRewardsAtomic,
    long ClaimableRewardsAtomic,
    long ClaimedStakesAtomic);

public sealed record BackendContractOptions
{
    public long ChainId { get; init; } = 42_161;
    public string NetworkName { get; init; } = "mainnet";
    public string EthereumRpcUrl { get; init; } = "";
    public string EthereumFallbackRpcUrls { get; init; } = "";
    public string? DeploymentManifestPath { get; init; }
    public string? ExpectedDeploymentNetwork { get; init; }
    public long? ExpectedDeploymentChainId { get; init; }
    public string? DeploymentLifecycleId { get; init; }
    public int ReadinessTimeoutSeconds { get; init; } = 5;
    public string TokenAddress { get; init; } = "";
    public string ServiceNodeRewardsAddress { get; init; } = "";
    public string ServiceNodeContributionFactoryAddress { get; init; } = "";
    public string RewardRatePoolAddress { get; init; } = "";
    public long StakingRequirementAtomic { get; init; } = 25_000L * 1_000_000_000L;
    public int MaxStakers { get; init; } = 10;
    public int HardFork { get; init; } = 21;
    public string Version { get; init; } = "deep";
    public int RewardAccrualIntervalSeconds { get; init; } = 30;
    public int RewardPulseSeconds { get; init; } = 120;
    public int ExitRequestTimeSeconds { get; init; } = 14 * 24 * 60 * 60;
    public int ChainBlockTimeMilliseconds { get; init; } = 250;
    public int QuorumSignatureTimeoutSeconds { get; init; } = 10;
    public int QuorumNonSignerThresholdMax { get; init; } = 4000;

    public string? StatePath { get; init; }
}

public sealed record BackendPriceOptions
{
    public string BaseUrl { get; init; } = "";

    public string DefaultToken { get; init; } = "xpnt";

    public string VsCurrency { get; init; } = "usd";

    public string EthereumRpcUrl { get; init; } = "https://arb1.arbitrum.io/rpc";
    public string EthereumFallbackRpcUrls { get; init; } = "";

    public string UniswapPoolAddress { get; init; } = "0x5d42A2b90867B813B753f5B49877Ea7c6bB2B603";

    public string BaseTokenAddress { get; init; } = "0x63b2cdb8b0d8774f1fdca91d24803698582a079f";

    public string QuoteTokenAddress { get; init; } = "0xaf88d065e77c8cc2239327c5edb3a432268e5831";

    public int TimeoutSeconds { get; init; } = 5;

    public IReadOnlyDictionary<string, string> TokenIds { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record BackendRegistryOptions
{
    public string BaseUrl { get; init; } = "";

    public int TimeoutSeconds { get; init; } = 5;

    public int HeartbeatGraceSeconds { get; init; } = 120;

    public int DecommissionGraceSeconds { get; init; } = 300;

    public int LiquidationGraceSeconds { get; init; } = 600;
}

public sealed record RegistrySigningNode(
    string NodeId,
    string BlsPublicKey,
    string SigningEndpoint,
    DateTimeOffset UpdatedAt,
    RegistryTransportStatus? TransportStatus,
    DateTimeOffset? TransportHealthySince,
    DateTimeOffset? TransportUnhealthySince);

public sealed record RegistryTransportStatus
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

public sealed record ServiceNodeObligationStatus
{
    public string NodeId { get; init; } = "";
    public long ContractId { get; init; }
    public string BlsPublicKey { get; init; } = "";
    public string Ed25519PublicKey { get; init; } = "";
    public string ChainStatus { get; init; } = "";
    public string Status { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public long? HeartbeatAgeSeconds { get; init; }
    public DateTimeOffset? TransportUnhealthySince { get; init; }
    public long? TransportUnhealthySeconds { get; init; }
    public long? DecommissionEligibleAtUnix { get; init; }
    public long? LiquidationEligibleAtUnix { get; init; }
    public bool RewardEligible { get; init; }
    public bool ExitSignatureEligible { get; init; }
    public bool LiquidationSignatureEligible { get; init; }
}

public sealed class ServiceNodeSignatureNotEligibleException : InvalidOperationException
{
    public ServiceNodeSignatureNotEligibleException(string message)
        : base(message)
    {
    }
}
