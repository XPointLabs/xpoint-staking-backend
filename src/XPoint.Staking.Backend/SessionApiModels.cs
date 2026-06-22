using System.Text.Json;
using System.Text.Json.Serialization;

namespace XPoint.Staking.Backend;

public enum ContributionContractStatus
{
    WaitForOperatorContrib = 0,
    OpenForPublicContrib = 1,
    WaitForFinalized = 2,
    Finalized = 3
}

public sealed record SessionNetworkInfo
{
    [JsonPropertyName("active_node_count")]
    public int ActiveNodeCount { get; init; }

    [JsonPropertyName("block_hash")]
    public string BlockHash { get; init; } = "";

    [JsonPropertyName("block_height")]
    public long BlockHeight { get; init; }

    [JsonPropertyName("block_timestamp")]
    public long BlockTimestamp { get; init; }

    [JsonPropertyName("hard_fork")]
    public int HardFork { get; init; }

    [JsonPropertyName("immutable_block_hash")]
    public string ImmutableBlockHash { get; init; } = "";

    [JsonPropertyName("immutable_block_height")]
    public long ImmutableBlockHeight { get; init; }

    [JsonPropertyName("max_stakers")]
    public int MaxStakers { get; init; }

    [JsonPropertyName("median_operator_fee")]
    public int MedianOperatorFee { get; init; }

    [JsonPropertyName("min_operator_contribution")]
    public long MinOperatorContribution { get; init; }

    [JsonPropertyName("nettype")]
    public string NetType { get; init; } = "mainnet";

    [JsonPropertyName("node_count")]
    public int NodeCount { get; init; }

    [JsonPropertyName("pulse_target_timestamp")]
    public long PulseTargetTimestamp { get; init; }

    [JsonPropertyName("staking_requirement")]
    public long StakingRequirement { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = "deep";
}

public record SessionResponseBase
{
    [JsonPropertyName("network")]
    public required SessionNetworkInfo Network { get; init; }

    [JsonPropertyName("t")]
    public double Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}

public sealed record SessionEventDto
{
    [JsonPropertyName("args")]
    public IReadOnlyDictionary<string, JsonElement> Args { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("block")]
    public long Block { get; init; }

    [JsonPropertyName("log_index")]
    public int LogIndex { get; init; }

    [JsonPropertyName("main_arg")]
    public string? MainArg { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("tx")]
    public string TransactionHash { get; init; } = "";
}

public sealed record ContributionContractContributorDto
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";

    [JsonPropertyName("beneficiary_address")]
    public string? BeneficiaryAddress { get; init; }

    [JsonPropertyName("amount")]
    public long Amount { get; init; }

    [JsonPropertyName("reserved")]
    public long Reserved { get; init; }
}

public sealed record ContributionContractDto
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";

    [JsonPropertyName("contributors")]
    public IReadOnlyList<ContributionContractContributorDto> Contributors { get; init; } =
        Array.Empty<ContributionContractContributorDto>();

    [JsonPropertyName("events")]
    public IReadOnlyList<SessionEventDto> Events { get; init; } = Array.Empty<SessionEventDto>();

    [JsonPropertyName("fee")]
    public int? Fee { get; init; }

    [JsonPropertyName("manual_finalize")]
    public bool ManualFinalize { get; init; }

    [JsonPropertyName("operator_address")]
    public string OperatorAddress { get; init; } = "";

    [JsonPropertyName("pubkey_bls")]
    public string? BlsPublicKey { get; init; }

    [JsonPropertyName("service_node_pubkey")]
    public string ServiceNodePubkey { get; init; } = "";

    [JsonPropertyName("status")]
    public ContributionContractStatus Status { get; init; }
}

public sealed record StakeContributorDto
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";

    [JsonPropertyName("beneficiary")]
    public string Beneficiary { get; init; } = "";

    [JsonPropertyName("amount")]
    public long Amount { get; init; }
}

public sealed record StakeDto
{
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("contract_id")]
    public int ContractId { get; init; }

    [JsonPropertyName("contributors")]
    public IReadOnlyList<StakeContributorDto> Contributors { get; init; } = Array.Empty<StakeContributorDto>();

    [JsonPropertyName("deregistration_height")]
    public long? DeregistrationHeight { get; init; }

    [JsonPropertyName("earned_downtime_blocks")]
    public long EarnedDowntimeBlocks { get; init; }

    [JsonPropertyName("events")]
    public IReadOnlyList<SessionEventDto> Events { get; init; } = Array.Empty<SessionEventDto>();

    [JsonPropertyName("exit_type")]
    public string? ExitType { get; init; }

    [JsonPropertyName("last_reward_block_height")]
    public long LastRewardBlockHeight { get; init; }

    [JsonPropertyName("last_uptime_proof")]
    public long LastUptimeProof { get; init; }

    [JsonPropertyName("liquidation_height")]
    public long? LiquidationHeight { get; init; }

    [JsonPropertyName("operator_address")]
    public string OperatorAddress { get; init; } = "";

    [JsonPropertyName("operator_fee")]
    public int OperatorFee { get; init; }

    [JsonPropertyName("pubkey_bls")]
    public string BlsPublicKey { get; init; } = "";

    [JsonPropertyName("pubkey_ed25519")]
    public string Ed25519PublicKey { get; init; } = "";

    [JsonPropertyName("registration_height")]
    public long RegistrationHeight { get; init; }

    [JsonPropertyName("requested_unlock_height")]
    public long RequestedUnlockHeight { get; init; }

    [JsonPropertyName("service_node_pubkey")]
    public string ServiceNodePubkey { get; init; } = "";

    [JsonPropertyName("service_node_version")]
    public IReadOnlyList<int>? ServiceNodeVersion { get; init; }

    [JsonPropertyName("staking_requirement")]
    public long StakingRequirement { get; init; }

    [JsonPropertyName("total_contributed")]
    public long TotalContributed { get; init; }
}

public sealed record VestingContractDto
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";

    [JsonPropertyName("beneficiary")]
    public string Beneficiary { get; init; } = "";

    [JsonPropertyName("initial_amount")]
    public long InitialAmount { get; init; }

    [JsonPropertyName("initial_beneficiary")]
    public string InitialBeneficiary { get; init; } = "";

    [JsonPropertyName("revoker")]
    public string Revoker { get; init; } = "";

    [JsonPropertyName("time_end")]
    public long TimeEnd { get; init; }

    [JsonPropertyName("time_start")]
    public long TimeStart { get; init; }

    [JsonPropertyName("transferable_beneficiary")]
    public bool TransferableBeneficiary { get; init; }
}

public sealed record RewardsInfoDto
{
    [JsonPropertyName("amount")]
    public long Amount { get; init; }

    [JsonPropertyName("lifetime_liquidated_stakes")]
    public long LifetimeLiquidatedStakes { get; init; }

    [JsonPropertyName("lifetime_locked_stakes")]
    public long LifetimeLockedStakes { get; init; }

    [JsonPropertyName("lifetime_rewards")]
    public long LifetimeRewards { get; init; }

    [JsonPropertyName("lifetime_unlocked_stakes")]
    public long LifetimeUnlockedStakes { get; init; }

    [JsonPropertyName("locked_stakes")]
    public long LockedStakes { get; init; }

    [JsonPropertyName("timelocked_stakes")]
    public long TimelockedStakes { get; init; }

    [JsonPropertyName("claimed_stakes")]
    public long ClaimedStakes { get; init; }

    [JsonPropertyName("claimed_rewards")]
    public long ClaimedRewards { get; init; }
}

public sealed record RegistrationDto
{
    [JsonPropertyName("operator")]
    public string Operator { get; init; } = "";

    [JsonPropertyName("pubkey_bls")]
    public string BlsPublicKey { get; init; } = "";

    [JsonPropertyName("pubkey_ed25519")]
    public string Ed25519PublicKey { get; init; } = "";

    [JsonPropertyName("sig_bls")]
    public string BlsSignature { get; init; } = "";

    [JsonPropertyName("sig_ed25519")]
    public string Ed25519Signature { get; init; } = "";

    [JsonPropertyName("timestamp")]
    public double Timestamp { get; init; }
}

public sealed record ContributionContractResponse : SessionResponseBase
{
    [JsonPropertyName("contracts")]
    public IReadOnlyList<ContributionContractDto> Contracts { get; init; } =
        Array.Empty<ContributionContractDto>();

    [JsonPropertyName("added_bls_keys")]
    public IReadOnlyDictionary<string, long> AddedBlsKeys { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ContributionContractByKeyResponse : SessionResponseBase
{
    [JsonPropertyName("contract")]
    public ContributionContractDto? Contract { get; init; }
}

public sealed record StakesResponse : SessionResponseBase
{
    [JsonPropertyName("contracts")]
    public IReadOnlyList<ContributionContractDto> Contracts { get; init; } =
        Array.Empty<ContributionContractDto>();

    [JsonPropertyName("stakes")]
    public IReadOnlyList<StakeDto> Stakes { get; init; } = Array.Empty<StakeDto>();

    [JsonPropertyName("vesting")]
    public IReadOnlyList<VestingContractDto> Vesting { get; init; } = Array.Empty<VestingContractDto>();

    [JsonPropertyName("added_bls_keys")]
    public IReadOnlyDictionary<string, long> AddedBlsKeys { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ContractNodesResponse : SessionResponseBase
{
    [JsonPropertyName("nodes")]
    public IReadOnlyDictionary<long, ContractNodeStatusDto> Nodes { get; init; } =
        new Dictionary<long, ContractNodeStatusDto>();
}

public sealed record ContractNodeStatusDto
{
    [JsonPropertyName("bls")]
    public string BlsPublicKey { get; init; } = "";

    [JsonPropertyName("ed25519")]
    public string Ed25519PublicKey { get; init; } = "";

    [JsonPropertyName("in")]
    public bool In { get; init; }
}

public sealed record NodesBlsKeysResponse : SessionResponseBase
{
    [JsonPropertyName("bls_keys")]
    public IReadOnlyDictionary<string, long> BlsKeys { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
}

public sealed record RewardsResponse : SessionResponseBase
{
    [JsonPropertyName("rewards")]
    public required RewardsInfoDto Rewards { get; init; }
}

public sealed record RewardsSignatureResponse : SessionResponseBase
{
    [JsonPropertyName("rewards")]
    public required RewardsSignatureDto Rewards { get; init; }
}

public sealed record RewardsSignatureDto
{
    [JsonPropertyName("aggregate_pubkey")]
    public string AggregatePublicKey { get; init; } = "";

    [JsonPropertyName("amount")]
    public long Amount { get; init; }

    [JsonPropertyName("height")]
    public long Height { get; init; }

    [JsonPropertyName("msg_to_sign")]
    public string MessageToSign { get; init; } = "";

    [JsonPropertyName("non_signer_indices")]
    public IReadOnlyList<long> NonSignerIndices { get; init; } = Array.Empty<long>();

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = "";
}

public sealed record BlsExitSignatureResponse : SessionResponseBase
{
    [JsonPropertyName("result")]
    public required BlsExitSignatureDto Result { get; init; }
}

public sealed record BlsExitSignatureDto
{
    [JsonPropertyName("bls_pubkey")]
    public string BlsPublicKey { get; init; } = "";

    [JsonPropertyName("msg_to_sign")]
    public string MessageToSign { get; init; } = "";

    [JsonPropertyName("non_signer_indices")]
    public IReadOnlyList<long> NonSignerIndices { get; init; } = Array.Empty<long>();

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = "";

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }
}

public sealed record ServiceNodeObligationsResponse : SessionResponseBase
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<ServiceNodeObligationDto> Nodes { get; init; } = Array.Empty<ServiceNodeObligationDto>();
}

public sealed record ServiceNodeObligationDto
{
    [JsonPropertyName("node_id")]
    public string NodeId { get; init; } = "";

    [JsonPropertyName("contract_id")]
    public long ContractId { get; init; }

    [JsonPropertyName("bls_public_key")]
    public string BlsPublicKey { get; init; } = "";

    [JsonPropertyName("service_node_pubkey")]
    public string ServiceNodePubkey { get; init; } = "";

    [JsonPropertyName("chain_status")]
    public string ChainStatus { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonPropertyName("last_heartbeat_at")]
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    [JsonPropertyName("heartbeat_age_seconds")]
    public long? HeartbeatAgeSeconds { get; init; }

    [JsonPropertyName("transport_unhealthy_since")]
    public DateTimeOffset? TransportUnhealthySince { get; init; }

    [JsonPropertyName("transport_unhealthy_seconds")]
    public long? TransportUnhealthySeconds { get; init; }

    [JsonPropertyName("decommission_eligible_at")]
    public long? DecommissionEligibleAtUnix { get; init; }

    [JsonPropertyName("liquidation_eligible_at")]
    public long? LiquidationEligibleAtUnix { get; init; }

    [JsonPropertyName("reward_eligible")]
    public bool RewardEligible { get; init; }

    [JsonPropertyName("exit_signature_eligible")]
    public bool ExitSignatureEligible { get; init; }

    [JsonPropertyName("liquidation_signature_eligible")]
    public bool LiquidationSignatureEligible { get; init; }
}

public sealed record ExitLiquidationListResponse : SessionResponseBase
{
    [JsonPropertyName("result")]
    public IReadOnlyList<ExitLiquidationListItemDto> Result { get; init; } =
        Array.Empty<ExitLiquidationListItemDto>();
}

public sealed record ExitLiquidationListItemDto
{
    [JsonPropertyName("info")]
    public required ExitLiquidationNodeInfoDto Info { get; init; }

    [JsonPropertyName("height")]
    public long Height { get; init; }

    [JsonPropertyName("liquidation_height")]
    public long LiquidationHeight { get; init; }

    [JsonPropertyName("service_node_pubkey")]
    public string ServiceNodePubkey { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "v2";
}

public sealed record ExitLiquidationNodeInfoDto
{
    [JsonPropertyName("bls_public_key")]
    public string BlsPublicKey { get; init; } = "";
}

public sealed record DailyRewardsResponse : SessionResponseBase
{
    [JsonPropertyName("rewards")]
    public IReadOnlyList<DailyRewardDto> Rewards { get; init; } = Array.Empty<DailyRewardDto>();
}

public sealed record DailyRewardDto
{
    [JsonPropertyName("block")]
    public long Block { get; init; }

    [JsonPropertyName("lifetime_rewards")]
    public long LifetimeRewards { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }
}

public sealed record RegistrationsResponse : SessionResponseBase
{
    [JsonPropertyName("registrations")]
    public IReadOnlyList<RegistrationDto> Registrations { get; init; } = Array.Empty<RegistrationDto>();
}

public sealed record HardForkInfoResponse : SessionResponseBase
{
    [JsonPropertyName("version_info")]
    public object VersionInfo { get; init; } = new
    {
        enabled = true,
        earliest_height = (long?)null,
        version = (int?)null
    };
}
