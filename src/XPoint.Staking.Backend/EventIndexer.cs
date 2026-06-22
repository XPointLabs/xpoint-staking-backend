using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace XPoint.Staking.Backend;

public sealed class EventIndexer
{
    private const long OperatorFeeDenominator = 10_000L;
    private const long DailyRewardRetentionSeconds = 26 * 60 * 60;
    private const long DailyRewardMinSampleSeconds = 60;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, IndexedEvent> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProjectedNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ContributionContractState> _contributionContracts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RewardAccumulator> _rewards = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<DailyRewardDto>> _dailyRewards = new(StringComparer.OrdinalIgnoreCase);
    private readonly BackendContractOptions _options;
    private readonly string? _statePath;
    private DateTimeOffset? _lastRewardAccrualAtUtc;
    private long _rewardRemainderAtomicSeconds;
    private long _stakingRequirementAtomic;
    private long _maxBlockNumber;
    private string _lastBlockHash = "";
    private long _attemptedIngest;
    private long _insertedIngest;
    private long _duplicateIngest;
    private long _staleNodeProjectionIgnored;
    private long _staleStatusIgnored;
    private long _corruptedStateRecoveries;
    private long _statePersistenceFailures;

    public EventIndexer(IOptions<BackendContractOptions> options)
    {
        _options = options.Value;
        _stakingRequirementAtomic = _options.StakingRequirementAtomic;
        _statePath = string.IsNullOrWhiteSpace(_options.StatePath)
            ? Path.Combine(AppContext.BaseDirectory, "artifacts", "staking-state.json")
            : _options.StatePath;

        LoadState();
    }

    public IngestEventResult Ingest(ChainEvent chainEvent)
    {
        Interlocked.Increment(ref _attemptedIngest);

        var id = GetEventId(chainEvent);
        var indexed = new IndexedEvent(id, chainEvent, DateTimeOffset.UtcNow);
        var inserted = _events.TryAdd(id, indexed);
        if (inserted)
        {
            Interlocked.Increment(ref _insertedIngest);

            var projectionStats = Project(indexed);
            if (projectionStats.StaleNodeProjectionIgnored)
            {
                Interlocked.Increment(ref _staleNodeProjectionIgnored);
            }

            if (projectionStats.StaleStatusIgnored)
            {
                Interlocked.Increment(ref _staleStatusIgnored);
            }

            PersistState();
        }
        else
        {
            Interlocked.Increment(ref _duplicateIngest);
        }

        return new IngestEventResult(id, inserted, _events.Count);
    }

    public EventIngestionStats GetIngestionStats()
    {
        return new EventIngestionStats(
            Interlocked.Read(ref _attemptedIngest),
            Interlocked.Read(ref _insertedIngest),
            Interlocked.Read(ref _duplicateIngest),
            Interlocked.Read(ref _staleNodeProjectionIgnored),
            Interlocked.Read(ref _staleStatusIgnored),
            Interlocked.Read(ref _corruptedStateRecoveries),
            Interlocked.Read(ref _statePersistenceFailures),
            _events.Count);
    }

    public IReadOnlyCollection<IndexedEvent> GetEvents(string? name = null)
    {
        var values = _events.Values.OrderBy(item => item.Event.BlockNumber).ThenBy(item => item.Event.LogIndex);
        return string.IsNullOrWhiteSpace(name)
            ? values.ToArray()
            : values.Where(item => string.Equals(item.Event.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public void UpdateChainTip(long blockNumber, string blockHash)
    {
        var previous = Interlocked.Read(ref _maxBlockNumber);
        while (blockNumber > previous)
        {
            var exchanged = Interlocked.CompareExchange(ref _maxBlockNumber, blockNumber, previous);
            if (exchanged == previous)
            {
                if (!string.IsNullOrWhiteSpace(blockHash))
                {
                    _lastBlockHash = blockHash;
                }

                return;
            }

            previous = exchanged;
        }
    }

    public IReadOnlyCollection<ProjectedNode> GetNodes()
    {
        return _nodes.Values.OrderBy(node => node.NodeId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ProjectedNode? GetNode(string nodeId)
    {
        return _nodes.TryGetValue(Normalize(nodeId), out var node) ? node : null;
    }

    public RewardState GetRewards(string address)
    {
        var key = Normalize(address);
        var rewards = _rewards.GetOrAdd(key, _ => new RewardAccumulator());
        return new RewardState(
            key,
            "XPNT",
            9,
            rewards.LifetimeRewardsAtomic,
            rewards.ClaimedRewardsAtomic,
            Math.Max(0, rewards.LifetimeRewardsAtomic - rewards.ClaimedRewardsAtomic),
            rewards.ClaimedStakesAtomic);
    }

    public RewardState ApplyRewardStateFromChain(
        string address,
        long lifetimeRewardsAtomic,
        long claimedRewardsAtomic)
    {
        var key = Normalize(address);
        lock (_gate)
        {
            var rewards = _rewards.GetOrAdd(key, _ => new RewardAccumulator());
            var previousLifetime = rewards.LifetimeRewardsAtomic;
            rewards.LifetimeRewardsAtomic = Math.Max(rewards.LifetimeRewardsAtomic, lifetimeRewardsAtomic);
            rewards.ClaimedRewardsAtomic = Math.Max(rewards.ClaimedRewardsAtomic, claimedRewardsAtomic);

            if (rewards.LifetimeRewardsAtomic > previousLifetime)
            {
                AddDailyRewardPoint(key, rewards.LifetimeRewardsAtomic, DateTimeOffset.UtcNow);
            }

            PersistState();
            return new RewardState(
                key,
                "XPNT",
                9,
                rewards.LifetimeRewardsAtomic,
                rewards.ClaimedRewardsAtomic,
                Math.Max(0, rewards.LifetimeRewardsAtomic - rewards.ClaimedRewardsAtomic),
                rewards.ClaimedStakesAtomic);
        }
    }

    public long ApplyRewardAccrual(
        long rewardRateAtomicPerPulse,
        DateTimeOffset accrualTimeUtc,
        IReadOnlySet<string>? rewardEligibleNodeIds = null)
    {
        if (rewardRateAtomicPerPulse <= 0)
        {
            return 0;
        }

        var pulseSeconds = Math.Max(1, _options.RewardPulseSeconds);
        var activeNodes = _nodes.Values
            .Where(node =>
                string.Equals(node.Status, "active", StringComparison.OrdinalIgnoreCase)
                && (rewardEligibleNodeIds is null || rewardEligibleNodeIds.Contains(node.NodeId)))
            .OrderBy(node => ParseLongOrZero(node.NodeId))
            .ToArray();

        lock (_gate)
        {
            if (_lastRewardAccrualAtUtc is null)
            {
                _lastRewardAccrualAtUtc = GetInitialRewardAccrualTime() ?? accrualTimeUtc;
            }

            if (activeNodes.Length == 0)
            {
                _lastRewardAccrualAtUtc = accrualTimeUtc;
                return 0;
            }

            var elapsedSeconds = (long)Math.Floor((accrualTimeUtc - _lastRewardAccrualAtUtc.Value).TotalSeconds);
            if (elapsedSeconds <= 0)
            {
                return 0;
            }

            var rewardAtomicSeconds = checked((rewardRateAtomicPerPulse * elapsedSeconds) + _rewardRemainderAtomicSeconds);
            var networkRewardAtomic = rewardAtomicSeconds / pulseSeconds;
            _rewardRemainderAtomicSeconds = rewardAtomicSeconds % pulseSeconds;
            _lastRewardAccrualAtUtc = accrualTimeUtc;

            if (networkRewardAtomic <= 0)
            {
                PersistState();
                return 0;
            }

            DistributeNetworkReward(networkRewardAtomic, activeNodes, accrualTimeUtc);
            PersistState();
            return networkRewardAtomic;
        }
    }

    public long GetRewardSignatureAmount(string address)
    {
        var rewards = GetRewards(address);
        return Math.Max(0, rewards.LifetimeRewardsAtomic + rewards.ClaimedStakesAtomic);
    }

    public IReadOnlyList<DailyRewardDto> GetDailyRewards(string address)
    {
        return _dailyRewards.TryGetValue(Normalize(address), out var rewards)
            ? rewards.OrderBy(item => item.Timestamp).ToArray()
            : Array.Empty<DailyRewardDto>();
    }

    public string GetAggregatePublicKeyHint()
    {
        var keys = GetAddedBlsKeys().Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return keys.Length == 0 ? "" : string.Join("", keys);
    }

    public SessionNetworkInfo GetNetworkInfo()
    {
        var runningNodes = _nodes.Values.Count(static node =>
            string.Equals(node.Status, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.Status, "exiting", StringComparison.OrdinalIgnoreCase));
        var multiContributorNodes = _nodes.Values
            .Where(static node => node.Contributors.Count > 1)
            .Select(static node => node.OperatorFeeBps)
            .Order()
            .ToArray();
        var medianOperatorFee = multiContributorNodes.Length switch
        {
            0 => 0,
            var odd when odd % 2 == 1 => multiContributorNodes[odd / 2],
            var even => (multiContributorNodes[(even / 2) - 1] + multiContributorNodes[even / 2]) / 2
        };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new SessionNetworkInfo
        {
            ActiveNodeCount = runningNodes,
            BlockHash = _lastBlockHash,
            BlockHeight = Interlocked.Read(ref _maxBlockNumber),
            BlockTimestamp = now,
            HardFork = _options.HardFork,
            ImmutableBlockHash = _lastBlockHash,
            ImmutableBlockHeight = Math.Max(0, Interlocked.Read(ref _maxBlockNumber) - 20),
            MaxStakers = _options.MaxStakers,
            MedianOperatorFee = medianOperatorFee,
            MinOperatorContribution = _stakingRequirementAtomic / 4,
            NetType = _options.NetworkName,
            NodeCount = _nodes.Count,
            PulseTargetTimestamp = now + 120,
            StakingRequirement = _stakingRequirementAtomic,
            Version = _options.Version
        };
    }

    public IReadOnlyCollection<ContributionContractDto> GetContributionContracts()
    {
        return _contributionContracts.Values
            .OrderByDescending(state => state.FirstBlock)
            .Select(ToDto)
            .ToArray();
    }

    public ContributionContractDto? GetContributionContractByNodePubkey(string serviceNodePubkey)
    {
        var normalized = NormalizeHexKey(serviceNodePubkey);
        return _contributionContracts.Values
            .Where(state => string.Equals(NormalizeHexKey(state.ServiceNodePubkey), normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(state => state.FirstBlock)
            .Select(ToDto)
            .FirstOrDefault();
    }

    public IReadOnlyCollection<ContributionContractDto> GetContributionContractsForWallet(string address)
    {
        var normalized = Normalize(address);
        return _contributionContracts.Values
            .Where(state =>
                string.Equals(Normalize(state.OperatorAddress), normalized, StringComparison.OrdinalIgnoreCase)
                || state.Contributors.Values.Any(contributor =>
                    string.Equals(Normalize(contributor.Address), normalized, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(state => state.FirstBlock)
            .Select(ToDto)
            .ToArray();
    }

    public IReadOnlyCollection<StakeDto> GetSessionStakes(string? addressOrNodePubkey = null)
    {
        var normalizedAddress = addressOrNodePubkey?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true
            ? Normalize(addressOrNodePubkey)
            : null;
        var normalizedNodePubkey = normalizedAddress is null && !string.IsNullOrWhiteSpace(addressOrNodePubkey)
            ? NormalizeHexKey(addressOrNodePubkey)
            : null;

        return _nodes.Values
            .Where(node =>
            {
                if (normalizedAddress is not null)
                {
                    return node.Contributors.Any(contributor =>
                        string.Equals(Normalize(contributor.Address), normalizedAddress, StringComparison.OrdinalIgnoreCase));
                }

                if (normalizedNodePubkey is not null)
                {
                    return string.Equals(NormalizeHexKey(node.ServiceNodePubkey), normalizedNodePubkey, StringComparison.OrdinalIgnoreCase);
                }

                return true;
            })
            .OrderBy(node => ParseLongOrZero(node.NodeId))
            .Select(ToStakeDto)
            .ToArray();
    }

    public IReadOnlyDictionary<long, ContractNodeStatusDto> GetContractNodes()
    {
        return _nodes.Values
            .Select(node => new
            {
                Id = ParseLongOrZero(node.NodeId),
                Node = node
            })
            .Where(item => item.Id > 0)
            .ToDictionary(
                item => item.Id,
                item => new ContractNodeStatusDto
                {
                    BlsPublicKey = FormatBlsPublicKey(
                        item.Node.BlsPublicKeyData,
                        item.Node.BlsPublicKeyX,
                        item.Node.BlsPublicKeyY),
                    Ed25519PublicKey = FormatUInt256Hex(item.Node.ServiceNodePubkey),
                    In = !string.Equals(item.Node.Status, "exited", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(item.Node.Status, "liquidated", StringComparison.OrdinalIgnoreCase)
                });
    }

    public IReadOnlyDictionary<string, long> GetAddedBlsKeys()
    {
        return GetContractNodes()
            .Where(static item => item.Value.In && !string.IsNullOrWhiteSpace(item.Value.BlsPublicKey))
            .ToDictionary(static item => item.Value.BlsPublicKey, static item => item.Key, StringComparer.OrdinalIgnoreCase);
    }

    public RewardsInfoDto GetSessionRewards(string address)
    {
        var projection = GetRewards(address);
        return GetSessionRewards(projection);
    }

    public RewardsInfoDto GetSessionRewards(RewardState projection)
    {
        return new RewardsInfoDto
        {
            Amount = projection.ClaimableRewardsAtomic,
            LifetimeRewards = projection.LifetimeRewardsAtomic,
            LifetimeLockedStakes = _nodes.Values
                .SelectMany(node => node.Contributors)
                .Where(contributor => string.Equals(Normalize(contributor.Address), projection.Address, StringComparison.OrdinalIgnoreCase))
                .Sum(contributor => contributor.AmountAtomic),
            LifetimeUnlockedStakes = projection.ClaimedStakesAtomic,
            LockedStakes = Math.Max(0, _nodes.Values
                .SelectMany(node => node.Contributors)
                .Where(contributor => string.Equals(Normalize(contributor.Address), projection.Address, StringComparison.OrdinalIgnoreCase))
                .Sum(contributor => contributor.AmountAtomic) - projection.ClaimedStakesAtomic),
            ClaimedRewards = projection.ClaimedRewardsAtomic,
            ClaimedStakes = projection.ClaimedStakesAtomic
        };
    }

    private void DistributeNetworkReward(
        long networkRewardAtomic,
        IReadOnlyList<ProjectedNode> activeNodes,
        DateTimeOffset accrualTimeUtc)
    {
        var remainingNetworkReward = networkRewardAtomic;
        for (var index = 0; index < activeNodes.Count; index++)
        {
            var nodesLeft = activeNodes.Count - index;
            var nodeReward = index == activeNodes.Count - 1
                ? remainingNetworkReward
                : remainingNetworkReward / nodesLeft;
            remainingNetworkReward -= nodeReward;
            DistributeNodeReward(activeNodes[index], nodeReward, accrualTimeUtc);
        }
    }

    private void DistributeNodeReward(ProjectedNode node, long nodeRewardAtomic, DateTimeOffset accrualTimeUtc)
    {
        if (nodeRewardAtomic <= 0 || node.Contributors.Count == 0)
        {
            return;
        }

        var contributors = node.Contributors
            .Where(static contributor => contributor.AmountAtomic > 0)
            .ToArray();
        if (contributors.Length == 0)
        {
            return;
        }

        var totalStakeAtomic = contributors.Sum(static contributor => contributor.AmountAtomic);
        if (totalStakeAtomic <= 0)
        {
            return;
        }

        var operatorFeeAtomic = 0L;
        var operatorContributor = contributors.FirstOrDefault(static contributor =>
            !string.IsNullOrWhiteSpace(contributor.Address));
        if (node.OperatorFeeBps > 0 && operatorContributor is not null)
        {
            operatorFeeAtomic = Math.Min(
                nodeRewardAtomic,
                (nodeRewardAtomic * Math.Min(node.OperatorFeeBps, (int)OperatorFeeDenominator)) / OperatorFeeDenominator);
            AddLifetimeReward(operatorContributor.Beneficiary, operatorFeeAtomic, accrualTimeUtc);
        }

        var sharedRewardAtomic = nodeRewardAtomic - operatorFeeAtomic;
        var allocatedSharedReward = 0L;
        for (var index = 0; index < contributors.Length; index++)
        {
            var contributor = contributors[index];
            var share = index == contributors.Length - 1
                ? sharedRewardAtomic - allocatedSharedReward
                : (sharedRewardAtomic * contributor.AmountAtomic) / totalStakeAtomic;
            allocatedSharedReward += share;
            AddLifetimeReward(contributor.Beneficiary, share, accrualTimeUtc);
        }
    }

    private void AddLifetimeReward(string address, long amountAtomic, DateTimeOffset accrualTimeUtc)
    {
        if (amountAtomic <= 0 || string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var key = Normalize(address);
        var rewards = _rewards.GetOrAdd(key, _ => new RewardAccumulator());
        rewards.LifetimeRewardsAtomic += amountAtomic;
        AddDailyRewardPoint(key, rewards.LifetimeRewardsAtomic, accrualTimeUtc);
    }

    private void AddDailyRewardPoint(string address, long lifetimeRewardsAtomic, DateTimeOffset accrualTimeUtc)
    {
        var timestamp = accrualTimeUtc.ToUnixTimeSeconds();
        var block = Interlocked.Read(ref _maxBlockNumber);
        var rewards = _dailyRewards.GetOrAdd(address, _ => []);
        lock (rewards)
        {
            var last = rewards.LastOrDefault();
            if (last is not null && timestamp - last.Timestamp < DailyRewardMinSampleSeconds)
            {
                rewards[^1] = last with
                {
                    Block = block,
                    LifetimeRewards = lifetimeRewardsAtomic,
                    Timestamp = timestamp
                };
            }
            else
            {
                rewards.Add(new DailyRewardDto
                {
                    Block = block,
                    LifetimeRewards = lifetimeRewardsAtomic,
                    Timestamp = timestamp
                });
            }

            rewards.RemoveAll(item => timestamp - item.Timestamp > DailyRewardRetentionSeconds);
        }
    }

    private DateTimeOffset? GetInitialRewardAccrualTime()
    {
        return _events.Values
            .Where(static item => string.Equals(item.Event.Name, "NewServiceNodeV2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Event.Name, "NewSeededServiceNode", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static item => item.IndexedAt)
            .Select(static item => (DateTimeOffset?)item.IndexedAt)
            .FirstOrDefault();
    }

    private ProjectionStats Project(IndexedEvent indexedEvent)
    {
        var chainEvent = indexedEvent.Event;
        TrackChainTip(chainEvent);

        switch (chainEvent.Name)
        {
            case "NewServiceNodeContributionContract":
            case "UpdatePubkeys":
            case "UpdateFee":
            case "UpdateReservedContributors":
            case "UpdateManualFinalize":
            case "NewContribution":
            case "OpenForPublicContribution":
            case "Filled":
            case "WithdrawContribution":
            case "UpdateStakerBeneficiary":
            case "Finalized":
            case "Reset":
                ProjectContributionContract(chainEvent);
                return ProjectionStats.None;
            case "NewServiceNodeV2":
            case "NewSeededServiceNode":
                return new ProjectionStats(ProjectNode(chainEvent), false);
            case "ServiceNodeExitRequest":
                return new ProjectionStats(false, UpdateStatus(chainEvent, "exiting"));
            case "ServiceNodeExit":
                var staleExitStatus = UpdateStatus(chainEvent, "exited");
                ApplyReturnedStake(chainEvent);
                return new ProjectionStats(false, staleExitStatus);
            case "ServiceNodeLiquidated":
                return new ProjectionStats(false, UpdateStatus(chainEvent, "liquidated"));
            case "RewardsBalanceUpdated":
            case "RewardsUpdated":
                ApplyRewardUpdate(chainEvent);
                return ProjectionStats.None;
            case "RewardsClaimed":
                ApplyRewardClaim(chainEvent);
                return ProjectionStats.None;
            case "StakingRequirementUpdated":
                _stakingRequirementAtomic = ReadLong(chainEvent.Args, "newRequirement") ?? _stakingRequirementAtomic;
                return ProjectionStats.None;
            default:
                return ProjectionStats.None;
        }
    }

    private void TrackChainTip(ChainEvent chainEvent)
    {
        UpdateChainTip(chainEvent.BlockNumber, chainEvent.BlockHash);
    }

    private void ProjectContributionContract(ChainEvent chainEvent)
    {
        var contractAddress = GetContributionContractAddress(chainEvent);
        if (contractAddress is null)
        {
            return;
        }

        var state = _contributionContracts.GetOrAdd(
            Normalize(contractAddress),
            key => new ContributionContractState
            {
                Address = key,
                FirstBlock = chainEvent.BlockNumber,
                LastBlock = chainEvent.BlockNumber
            });

        lock (state)
        {
            state.LastBlock = Math.Max(state.LastBlock, chainEvent.BlockNumber);
            state.Events.Add(new IndexedEvent(GetEventId(chainEvent), chainEvent, DateTimeOffset.UtcNow));

            switch (chainEvent.Name)
            {
                case "NewServiceNodeContributionContract":
                    state.Address = Normalize(contractAddress);
                    state.OperatorAddress = ReadString(chainEvent.Args, "operator") ?? state.OperatorAddress;
                    state.ServiceNodePubkey = FormatUInt256Hex(
                        ReadString(chainEvent.Args, "serviceNodePubkey") ?? state.ServiceNodePubkey);
                    state.Status = ContributionContractStatus.WaitForOperatorContrib;
                    break;
                case "UpdatePubkeys":
                    state.BlsPublicKey = FormatBlsPublicKey(
                        ReadNestedString(chainEvent.Args, "newBLSPubkey", "data"),
                        ReadNestedString(chainEvent.Args, "newBLSPubkey", "X", "x"),
                        ReadNestedString(chainEvent.Args, "newBLSPubkey", "Y", "y"));
                    state.ServiceNodePubkey = FormatUInt256Hex(
                        ReadString(chainEvent.Args, "newEd25519Pubkey") ?? state.ServiceNodePubkey);
                    break;
                case "UpdateFee":
                    state.Fee = (int)(ReadLong(chainEvent.Args, "newFee") ?? state.Fee ?? 0);
                    break;
                case "UpdateManualFinalize":
                    state.ManualFinalize = ReadBool(chainEvent.Args, "newValue") ?? state.ManualFinalize;
                    break;
                case "UpdateReservedContributors":
                    ApplyReservedContributors(state, chainEvent);
                    break;
                case "NewContribution":
                    ApplyContribution(state, chainEvent);
                    break;
                case "WithdrawContribution":
                    ApplyWithdrawContribution(state, chainEvent);
                    break;
                case "UpdateStakerBeneficiary":
                    ApplyBeneficiaryUpdate(state, chainEvent);
                    break;
                case "OpenForPublicContribution":
                    state.Status = ContributionContractStatus.OpenForPublicContrib;
                    break;
                case "Filled":
                    state.Status = ContributionContractStatus.WaitForFinalized;
                    break;
                case "Finalized":
                    state.Status = ContributionContractStatus.Finalized;
                    break;
                case "Reset":
                    state.Status = ContributionContractStatus.WaitForOperatorContrib;
                    state.Contributors.Clear();
                    break;
            }
        }
    }

    private bool ProjectNode(ChainEvent chainEvent)
    {
        var nodeId = ReadString(chainEvent.Args, "serviceNodeID", "serviceNodeId", "nodeId", "id")
            ?? ReadString(chainEvent.Args, "serviceNodePubkey", "ed25519Pubkey")
            ?? $"{chainEvent.TransactionHash}:{chainEvent.LogIndex}";

        var contributors = ReadContributors(chainEvent.Args);
        var stakeAtomic = contributors.Sum(contributor => contributor.AmountAtomic);
        if (stakeAtomic == 0)
        {
            stakeAtomic = ReadLong(chainEvent.Args, "stakeAtomic", "deposit", "stakingRequirement") ?? 0;
        }

        var operatorAddress = ReadString(chainEvent.Args, "operator", "operatorAddress")
            ?? contributors.FirstOrDefault()?.Address
            ?? ReadString(chainEvent.Args, "initiator")
            ?? "";
        var rewardsAddress = contributors.FirstOrDefault()?.Beneficiary ?? operatorAddress;

        var node = new ProjectedNode
        {
            NodeId = Normalize(nodeId),
            ServiceNodePubkey = ReadNestedString(chainEvent.Args, "serviceNode", "serviceNodePubkey")
                ?? ReadString(chainEvent.Args, "serviceNodePubkey", "ed25519Pubkey")
                ?? Normalize(nodeId),
            BlsPublicKeyData = ReadNestedString(chainEvent.Args, "blsPubkey", "data")
                ?? ReadNestedString(chainEvent.Args, "pubkey", "data")
                ?? ReadString(chainEvent.Args, "blsData")
                ?? "",
            OperatorAddress = operatorAddress,
            RewardsAddress = rewardsAddress,
            BlsPublicKeyX = ReadNestedString(chainEvent.Args, "blsPubkey", "x", "X")
                ?? ReadNestedString(chainEvent.Args, "pubkey", "x", "X")
                ?? ReadString(chainEvent.Args, "blsX")
                ?? "",
            BlsPublicKeyY = ReadNestedString(chainEvent.Args, "blsPubkey", "y", "Y")
                ?? ReadNestedString(chainEvent.Args, "pubkey", "y", "Y")
                ?? ReadString(chainEvent.Args, "blsY")
                ?? "",
            OperatorFeeBps = (int)(ReadNestedLong(chainEvent.Args, "serviceNode", "fee")
                ?? ReadNestedLong(chainEvent.Args, "serviceNodeParams", "fee")
                ?? ReadLong(chainEvent.Args, "operatorFeeBps", "fee")
                ?? 0),
            StakeAtomic = stakeAtomic,
            Contributors = contributors,
            Status = "active",
            RegistrationBlockNumber = chainEvent.BlockNumber,
            LastBlockNumber = chainEvent.BlockNumber
        };

        var staleIgnored = false;
        _nodes.AddOrUpdate(
            node.NodeId,
            _ => node,
            (_, existing) =>
            {
                if (existing.LastBlockNumber > chainEvent.BlockNumber)
                {
                    staleIgnored = true;
                    return existing;
                }

                return node;
            });

        return staleIgnored;
    }

    private bool UpdateStatus(ChainEvent chainEvent, string status)
    {
        var nodeId = ReadString(chainEvent.Args, "serviceNodeID", "serviceNodeId", "nodeId", "id");
        if (nodeId is null)
        {
            return false;
        }

        var key = Normalize(nodeId);
        var isExiting = string.Equals(status, "exiting", StringComparison.OrdinalIgnoreCase);
        var staleIgnored = false;
        _nodes.AddOrUpdate(
            key,
            _ => new ProjectedNode
            {
                NodeId = key,
                Status = status,
                ExitRequestedBlockNumber = isExiting ? chainEvent.BlockNumber : null,
                LastBlockNumber = chainEvent.BlockNumber
            },
            (_, existing) =>
            {
                if (existing.LastBlockNumber > chainEvent.BlockNumber)
                {
                    staleIgnored = true;
                    return existing;
                }

                if (isExiting
                    && string.Equals(existing.Status, "exiting", StringComparison.OrdinalIgnoreCase)
                    && existing.ExitRequestedBlockNumber is not null)
                {
                    staleIgnored = true;
                    return existing;
                }

                return existing with
                {
                    Status = status,
                    ExitRequestedBlockNumber = isExiting
                        ? chainEvent.BlockNumber
                        : existing.ExitRequestedBlockNumber,
                    LastBlockNumber = chainEvent.BlockNumber
                };
            });

        return staleIgnored;
    }

    private void ApplyRewardUpdate(ChainEvent chainEvent)
    {
        var address = ReadString(chainEvent.Args, "recipientAddress", "recipient", "address", "wallet");
        if (address is null)
        {
            return;
        }

        var lifetime = ReadLong(chainEvent.Args, "lifetimeRewards", "recipientRewards", "rewards", "amount") ?? 0;
        var rewards = _rewards.GetOrAdd(Normalize(address), _ => new RewardAccumulator());
        rewards.LifetimeRewardsAtomic = Math.Max(rewards.LifetimeRewardsAtomic, lifetime);
    }

    private void ApplyRewardClaim(ChainEvent chainEvent)
    {
        var address = ReadString(chainEvent.Args, "recipientAddress", "recipient", "claimer", "address", "wallet");
        if (address is null)
        {
            return;
        }

        var rewards = _rewards.GetOrAdd(Normalize(address), _ => new RewardAccumulator());
        rewards.ClaimedRewardsAtomic += ReadLong(chainEvent.Args, "amount", "claimedRewards", "rewards") ?? 0;
    }

    private void ApplyReturnedStake(ChainEvent chainEvent)
    {
        var address = ReadString(chainEvent.Args, "recipientAddress", "recipient", "operator", "initiator", "address", "wallet");
        if (address is null)
        {
            return;
        }

        var rewards = _rewards.GetOrAdd(Normalize(address), _ => new RewardAccumulator());
        rewards.ClaimedStakesAtomic += ReadLong(chainEvent.Args, "amount", "returnedAmount", "returnedStake", "stake") ?? 0;
    }

    private static IReadOnlyList<ProjectedContributor> ReadContributors(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("contributors", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ProjectedContributor>();
        }

        var result = new List<ProjectedContributor>();
        foreach (var item in element.EnumerateArray())
        {
            var staker = item.TryGetProperty("staker", out var stakerValue) ? stakerValue : item;
            var address = ReadPropertyAsString(staker, "addr", "address") ?? "";
            var beneficiary = ReadPropertyAsString(staker, "beneficiary") ?? address;
            var amount = ReadPropertyAsLong(item, "stakedAmount", "amountAtomic", "amount") ?? 0;
            result.Add(new ProjectedContributor
            {
                Address = address,
                Beneficiary = beneficiary,
                AmountAtomic = amount
            });
        }

        return result;
    }

    private static void ApplyReservedContributors(ContributionContractState state, ChainEvent chainEvent)
    {
        if (!chainEvent.Args.TryGetValue("newReservedContributors", out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in element.EnumerateArray())
        {
            var address = ReadPropertyAsString(item, "addr", "address");
            if (address is null)
            {
                continue;
            }

            var key = Normalize(address);
            var contributor = state.Contributors.TryGetValue(key, out var existing)
                ? existing
                : new ContributionContributorState { Address = key };
            contributor.Reserved = ReadPropertyAsLong(item, "amount") ?? contributor.Reserved;
            state.Contributors[key] = contributor;
        }
    }

    private static void ApplyContribution(ContributionContractState state, ChainEvent chainEvent)
    {
        var address = ReadString(chainEvent.Args, "contributor");
        if (address is null)
        {
            return;
        }

        var key = Normalize(address);
        var contributor = state.Contributors.TryGetValue(key, out var existing)
            ? existing
            : new ContributionContributorState { Address = key };
        contributor.BeneficiaryAddress = ReadString(chainEvent.Args, "beneficiary") ?? contributor.BeneficiaryAddress ?? key;
        contributor.Amount += ReadLong(chainEvent.Args, "amount") ?? 0;
        state.Contributors[key] = contributor;

        if (state.Status == ContributionContractStatus.WaitForOperatorContrib)
        {
            state.Status = ContributionContractStatus.OpenForPublicContrib;
        }
    }

    private static void ApplyWithdrawContribution(ContributionContractState state, ChainEvent chainEvent)
    {
        var address = ReadString(chainEvent.Args, "contributor");
        if (address is null || !state.Contributors.TryGetValue(Normalize(address), out var contributor))
        {
            return;
        }

        contributor.Amount = Math.Max(0, contributor.Amount - (ReadLong(chainEvent.Args, "amount") ?? 0));
    }

    private static void ApplyBeneficiaryUpdate(ContributionContractState state, ChainEvent chainEvent)
    {
        var address = ReadString(chainEvent.Args, "staker");
        if (address is null || !state.Contributors.TryGetValue(Normalize(address), out var contributor))
        {
            return;
        }

        contributor.BeneficiaryAddress = ReadString(chainEvent.Args, "newBeneficiary") ?? contributor.BeneficiaryAddress;
    }

    private static string? GetContributionContractAddress(ChainEvent chainEvent)
    {
        if (string.Equals(chainEvent.Name, "NewServiceNodeContributionContract", StringComparison.OrdinalIgnoreCase))
        {
            return ReadString(chainEvent.Args, "contributorContract") ?? chainEvent.MainArg;
        }

        return chainEvent.MainArg ?? chainEvent.Address;
    }

    private ContributionContractDto ToDto(ContributionContractState state)
    {
        lock (state)
        {
            return new ContributionContractDto
            {
                Address = state.Address,
                Contributors = state.Contributors.Values
                    .OrderBy(contributor => contributor.Address, StringComparer.OrdinalIgnoreCase)
                    .Select(static contributor => new ContributionContractContributorDto
                    {
                        Address = contributor.Address,
                        BeneficiaryAddress = contributor.BeneficiaryAddress,
                        Amount = contributor.Amount,
                        Reserved = contributor.Reserved
                    })
                    .ToArray(),
                Events = state.Events
                    .OrderBy(item => item.Event.BlockNumber)
                    .ThenBy(item => item.Event.LogIndex)
                    .Select(ToSessionEventDto)
                    .ToArray(),
                Fee = state.Fee,
                ManualFinalize = state.ManualFinalize,
                OperatorAddress = state.OperatorAddress,
                BlsPublicKey = string.IsNullOrWhiteSpace(state.BlsPublicKey) ? null : state.BlsPublicKey,
                ServiceNodePubkey = state.ServiceNodePubkey,
                Status = state.Status
            };
        }
    }

    private StakeDto ToStakeDto(ProjectedNode node)
    {
        var contractId = (int)Math.Min(int.MaxValue, ParseLongOrZero(node.NodeId));
        var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var registrationHeight = node.RegistrationBlockNumber > 0
            ? node.RegistrationBlockNumber
            : node.LastBlockNumber;
        return new StakeDto
        {
            Active = string.Equals(node.Status, "active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.Status, "exiting", StringComparison.OrdinalIgnoreCase),
            ContractId = contractId,
            Contributors = node.Contributors.Select(static contributor => new StakeContributorDto
            {
                Address = contributor.Address,
                Beneficiary = contributor.Beneficiary,
                Amount = contributor.AmountAtomic
            }).ToArray(),
            Events = GetEventsForServiceNode(node.NodeId).Select(ToSessionEventDto).ToArray(),
            ExitType = string.Equals(node.Status, "exited", StringComparison.OrdinalIgnoreCase) ? "exit" : null,
            LastRewardBlockHeight = node.LastBlockNumber,
            LastUptimeProof = nowUnixSeconds,
            OperatorAddress = node.OperatorAddress,
            OperatorFee = node.OperatorFeeBps,
            BlsPublicKey = FormatBlsPublicKey(node.BlsPublicKeyData, node.BlsPublicKeyX, node.BlsPublicKeyY),
            Ed25519PublicKey = FormatUInt256Hex(node.ServiceNodePubkey),
            RegistrationHeight = registrationHeight,
            RequestedUnlockHeight = string.Equals(node.Status, "exiting", StringComparison.OrdinalIgnoreCase)
                ? GetRequestedUnlockHeight(node)
                : 0,
            ServiceNodePubkey = FormatUInt256Hex(node.ServiceNodePubkey),
            ServiceNodeVersion = new[] { 1, 0, 0 },
            StakingRequirement = _stakingRequirementAtomic,
            TotalContributed = node.StakeAtomic
        };
    }

    private long GetRequestedUnlockHeight(ProjectedNode node)
    {
        if (node.ExitRequestedBlockNumber is not { } exitRequestedBlock)
        {
            return 0;
        }

        var blockTimeMs = Math.Max(1, _options.ChainBlockTimeMilliseconds);
        var waitBlocks = (long)Math.Ceiling(_options.ExitRequestTimeSeconds * 1000.0 / blockTimeMs);
        return exitRequestedBlock + waitBlocks;
    }

    private IEnumerable<IndexedEvent> GetEventsForServiceNode(string nodeId)
    {
        return _events.Values
            .Where(item =>
                string.Equals(
                    ReadString(item.Event.Args, "serviceNodeID", "serviceNodeId", "nodeId", "id"),
                    nodeId,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Event.BlockNumber)
            .ThenBy(item => item.Event.LogIndex);
    }

    private static SessionEventDto ToSessionEventDto(IndexedEvent indexedEvent)
    {
        return new SessionEventDto
        {
            Args = indexedEvent.Event.Args,
            Block = indexedEvent.Event.BlockNumber,
            LogIndex = indexedEvent.Event.LogIndex,
            MainArg = indexedEvent.Event.MainArg ?? GetContributionContractAddress(indexedEvent.Event),
            Name = indexedEvent.Event.Name,
            TransactionHash = indexedEvent.Event.TransactionHash
        };
    }

    private static string GetEventId(ChainEvent chainEvent)
    {
        return $"{chainEvent.ChainId}:{chainEvent.TransactionHash}:{chainEvent.LogIndex}";
    }

    private static string? ReadString(IReadOnlyDictionary<string, JsonElement> args, params string[] names)
    {
        foreach (var name in names)
        {
            if (args.TryGetValue(name, out var value))
            {
                return ElementToString(value);
            }
        }

        return null;
    }

    private static long? ReadLong(IReadOnlyDictionary<string, JsonElement> args, params string[] names)
    {
        foreach (var name in names)
        {
            if (args.TryGetValue(name, out var value))
            {
                return ElementToLong(value);
            }
        }

        return null;
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, JsonElement> args, params string[] names)
    {
        foreach (var name in names)
        {
            if (args.TryGetValue(name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String when bool.TryParse(value.GetString(), out var result) => result,
                    _ => null
                };
            }
        }

        return null;
    }

    private static string? ReadNestedString(IReadOnlyDictionary<string, JsonElement> args, string objectName, params string[] propertyNames)
    {
        return args.TryGetValue(objectName, out var value) ? ReadPropertyAsString(value, propertyNames) : null;
    }

    private static long? ReadNestedLong(IReadOnlyDictionary<string, JsonElement> args, string objectName, params string[] propertyNames)
    {
        return args.TryGetValue(objectName, out var value) ? ReadPropertyAsLong(value, propertyNames) : null;
    }

    private static string? ReadPropertyAsString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
            {
                return ElementToString(value);
            }
        }

        return null;
    }

    private static long? ReadPropertyAsLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
            {
                return ElementToLong(value);
            }
        }

        return null;
    }

    private static string? ElementToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static long? ElementToLong(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static string FormatBlsPublicKey(string? data, string? x, string? y)
    {
        var normalizedData = NormalizeFixedHex(data, 128);
        if (!string.IsNullOrWhiteSpace(normalizedData))
        {
            return normalizedData;
        }

        return string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y)
            ? ""
            : $"{FormatUInt256Hex(x)}{FormatUInt256Hex(y)}";
    }

    private static string NormalizeFixedHex(string? value, int expectedBytes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        return normalized.Length == expectedBytes * 2 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToLowerInvariant()
            : "";
    }

    private static string FormatUInt256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length is 64 or 128 && normalized.All(Uri.IsHexDigit))
        {
            return normalized.PadLeft(64, '0').ToLowerInvariant();
        }

        if (BigInteger.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            var hex = number.ToString("x", CultureInfo.InvariantCulture);
            while (hex.Length > 64 && hex.StartsWith('0'))
            {
                hex = hex[1..];
            }

            return hex.PadLeft(64, '0').ToLowerInvariant();
        }

        return normalized.ToLowerInvariant();
    }

    private static string NormalizeHexKey(string value)
    {
        return FormatUInt256Hex(value).TrimStart('0').PadLeft(1, '0');
    }

    private static long ParseLongOrZero(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private void LoadState()
    {
        if (_statePath is null || !File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            var snapshot = JsonSerializer.Deserialize<EventSnapshot>(json, SerializerOptions);
            if (snapshot?.Events is null)
            {
                RecoverCorruptedStateFile();
                return;
            }

            foreach (var indexedEvent in snapshot.Events)
            {
                if (_events.TryAdd(indexedEvent.Id, indexedEvent))
                {
                    Project(indexedEvent);
                }
            }

            if (snapshot.Rewards is not null)
            {
                _rewards.Clear();
                foreach (var (address, reward) in snapshot.Rewards)
                {
                    _rewards[Normalize(address)] = new RewardAccumulator
                    {
                        LifetimeRewardsAtomic = reward.LifetimeRewardsAtomic,
                        ClaimedRewardsAtomic = reward.ClaimedRewardsAtomic,
                        ClaimedStakesAtomic = reward.ClaimedStakesAtomic
                    };
                }
            }

            if (snapshot.DailyRewards is not null)
            {
                _dailyRewards.Clear();
                foreach (var (address, rewards) in snapshot.DailyRewards)
                {
                    _dailyRewards[Normalize(address)] = rewards
                        .OrderBy(item => item.Timestamp)
                        .ToList();
                }
            }

            _lastRewardAccrualAtUtc = snapshot.LastRewardAccrualAtUtc;
            _rewardRemainderAtomicSeconds = snapshot.RewardRemainderAtomicSeconds;

            Interlocked.Exchange(ref _attemptedIngest, _events.Count);
            Interlocked.Exchange(ref _insertedIngest, _events.Count);
        }
        catch (JsonException)
        {
            RecoverCorruptedStateFile();
        }
        catch (IOException)
        {
            RecoverCorruptedStateFile();
        }
    }

    private void RecoverCorruptedStateFile()
    {
        if (_statePath is null || !File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var backupPath = $"{_statePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak";
            File.Move(_statePath, backupPath, true);
            Interlocked.Increment(ref _corruptedStateRecoveries);
        }
        catch
        {
            // Best effort: a damaged state file should not block startup.
        }
    }

    private void PersistState()
    {
        if (_statePath is null)
        {
            return;
        }

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var snapshot = new EventSnapshot(
                _events.Values.OrderBy(item => item.Event.BlockNumber).ThenBy(item => item.Event.LogIndex).ToArray(),
                _rewards.ToDictionary(
                    item => item.Key,
                    item => new RewardSnapshot(
                        item.Value.LifetimeRewardsAtomic,
                        item.Value.ClaimedRewardsAtomic,
                        item.Value.ClaimedStakesAtomic),
                    StringComparer.OrdinalIgnoreCase),
                _dailyRewards.ToDictionary(
                    item => item.Key,
                    item =>
                    {
                        lock (item.Value)
                        {
                            return (IReadOnlyList<DailyRewardDto>)item.Value.ToArray();
                        }
                    },
                    StringComparer.OrdinalIgnoreCase),
                _lastRewardAccrualAtUtc,
                _rewardRemainderAtomicSeconds);

            try
            {
                File.WriteAllText(_statePath, JsonSerializer.Serialize(snapshot, SerializerOptions));
            }
            catch (IOException)
            {
                Interlocked.Increment(ref _statePersistenceFailures);
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _statePersistenceFailures);
            }
        }
    }

    private sealed class RewardAccumulator
    {
        public long LifetimeRewardsAtomic;
        public long ClaimedRewardsAtomic;
        public long ClaimedStakesAtomic;
    }

    private sealed record ProjectionStats(bool StaleNodeProjectionIgnored, bool StaleStatusIgnored)
    {
        public static ProjectionStats None { get; } = new(false, false);
    }

    private sealed record EventSnapshot(
        IReadOnlyCollection<IndexedEvent> Events,
        IReadOnlyDictionary<string, RewardSnapshot>? Rewards = null,
        IReadOnlyDictionary<string, IReadOnlyList<DailyRewardDto>>? DailyRewards = null,
        DateTimeOffset? LastRewardAccrualAtUtc = null,
        long RewardRemainderAtomicSeconds = 0);

    private sealed record RewardSnapshot(
        long LifetimeRewardsAtomic,
        long ClaimedRewardsAtomic,
        long ClaimedStakesAtomic);

    private sealed class ContributionContractState
    {
        public string Address { get; set; } = "";
        public string OperatorAddress { get; set; } = "";
        public string ServiceNodePubkey { get; set; } = "";
        public string? BlsPublicKey { get; set; }
        public int? Fee { get; set; }
        public bool ManualFinalize { get; set; }
        public ContributionContractStatus Status { get; set; } = ContributionContractStatus.WaitForOperatorContrib;
        public long FirstBlock { get; init; }
        public long LastBlock { get; set; }
        public Dictionary<string, ContributionContributorState> Contributors { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<IndexedEvent> Events { get; } = [];
    }

    private sealed class ContributionContributorState
    {
        public string Address { get; init; } = "";
        public string? BeneficiaryAddress { get; set; }
        public long Amount { get; set; }
        public long Reserved { get; set; }
    }
}
