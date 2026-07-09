using System.Globalization;
using System.Numerics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Options;

namespace XPoint.Staking.Backend;

public sealed class RewardStateService
{
    private static readonly TimeSpan RewardSigningQuoteTtl = TimeSpan.FromSeconds(30);

    private readonly EventIndexer _indexer;
    private readonly EthereumJsonRpcClient _rpc;
    private readonly IOptions<BackendContractOptions> _options;
    private readonly ILogger<RewardStateService> _logger;
    private readonly ConcurrentDictionary<string, RewardSigningQuote> _rewardSigningQuotes =
        new(StringComparer.OrdinalIgnoreCase);
    private long _lastQuotePruneUnixSeconds;

    public RewardStateService(
        EventIndexer indexer,
        EthereumJsonRpcClient rpc,
        IOptions<BackendContractOptions> options,
        ILogger<RewardStateService> logger)
    {
        _indexer = indexer;
        _rpc = rpc;
        _options = options;
        _logger = logger;
    }

    public async Task<RewardState> GetRewardsAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var normalizedAddress = NormalizeRewardAddress(address);
        var projected = _indexer.GetRewards(normalizedAddress);
        var onChain = await TryGetOnChainRecipientAsync(normalizedAddress, cancellationToken).ConfigureAwait(false);
        if (onChain is null)
        {
            return projected;
        }

        return _indexer.ApplyRewardStateFromChain(
            normalizedAddress,
            Math.Max(projected.LifetimeRewardsAtomic, onChain.Value.RewardsAtomic),
            Math.Max(projected.ClaimedRewardsAtomic, onChain.Value.ClaimedAtomic));
    }

    public async Task<RewardsInfoDto> GetSessionRewardsAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var rewards = await GetRewardsAsync(address, cancellationToken).ConfigureAwait(false);
        return _indexer.GetSessionRewards(rewards);
    }

    public async Task<long> GetRewardSignatureAmountAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var rewards = await GetRewardsAsync(address, cancellationToken).ConfigureAwait(false);
        return Math.Max(0, rewards.LifetimeRewardsAtomic + rewards.ClaimedStakesAtomic);
    }

    public async Task<RewardSigningQuote> CreateRewardSigningQuoteAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var normalizedAddress = NormalizeRewardAddress(address);
        var amount = await GetRewardSignatureAmountAsync(normalizedAddress, cancellationToken)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        PruneExpiredQuotes(now);

        var quote = new RewardSigningQuote(
            GenerateQuoteId(),
            normalizedAddress,
            amount,
            now.Add(RewardSigningQuoteTtl).ToUnixTimeSeconds());
        _rewardSigningQuotes[quote.QuoteId] = quote;
        return quote;
    }

    public RewardSigningQuote? GetRewardSigningQuote(
        string address,
        string quoteId)
    {
        if (string.IsNullOrWhiteSpace(quoteId))
        {
            return null;
        }

        var normalizedAddress = NormalizeRewardAddress(address);
        if (!_rewardSigningQuotes.TryGetValue(quoteId.Trim(), out var quote))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (quote.ExpiresAtUnixSeconds < now.ToUnixTimeSeconds())
        {
            _rewardSigningQuotes.TryRemove(quote.QuoteId, out _);
            return null;
        }

        return string.Equals(quote.Address, normalizedAddress, StringComparison.OrdinalIgnoreCase)
            ? quote
            : null;
    }

    private async Task<OnChainRecipient?> TryGetOnChainRecipientAsync(
        string normalizedAddress,
        CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.EthereumRpcUrl)
            || string.IsNullOrWhiteSpace(options.ServiceNodeRewardsAddress))
        {
            return null;
        }

        try
        {
            var selector = Epoche.Keccak256.ComputeEthereumFunctionSelector("recipients(address)", true);
            var data = selector + normalizedAddress[2..].PadLeft(64, '0');
            var result = await _rpc.EthCallAsync(
                options.EthereumRpcUrl,
                options.EthereumFallbackRpcUrls,
                options.ServiceNodeRewardsAddress,
                data,
                cancellationToken).ConfigureAwait(false);

            return DecodeRecipient(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read on-chain reward recipient state for {Address}; using local projection.",
                normalizedAddress);
            return null;
        }
    }

    private static string NormalizeRewardAddress(string address)
    {
        if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && address.Length == 42)
        {
            return EthereumJsonRpcClient.NormalizeAddress(address);
        }

        return address.Trim().ToLowerInvariant();
    }

    private static OnChainRecipient DecodeRecipient(string ethCallResult)
    {
        var hex = EthereumJsonRpcClient.Strip0x(ethCallResult);
        if (hex.Length < 128)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"recipients(address) returned {hex.Length / 2} bytes; expected 64 bytes."));
        }

        return new OnChainRecipient(
            ParseUInt256ToInt64(hex[..64]),
            ParseUInt256ToInt64(hex.Substring(64, 64)));
    }

    private static long ParseUInt256ToInt64(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        if (value > long.MaxValue)
        {
            throw new OverflowException("Reward value does not fit into Int64.");
        }

        return (long)value;
    }

    private readonly record struct OnChainRecipient(long RewardsAtomic, long ClaimedAtomic);

    private void PruneExpiredQuotes(DateTimeOffset now)
    {
        var nowUnixSeconds = now.ToUnixTimeSeconds();
        if (nowUnixSeconds - Interlocked.Read(ref _lastQuotePruneUnixSeconds) < 5)
        {
            return;
        }

        Interlocked.Exchange(ref _lastQuotePruneUnixSeconds, nowUnixSeconds);
        foreach (var item in _rewardSigningQuotes)
        {
            if (item.Value.ExpiresAtUnixSeconds < nowUnixSeconds)
            {
                _rewardSigningQuotes.TryRemove(item.Key, out _);
            }
        }
    }

    private static string GenerateQuoteId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

public sealed record RewardSigningQuote(
    [property: JsonPropertyName("quoteId")]
    string QuoteId,
    [property: JsonPropertyName("address")]
    string Address,
    [property: JsonPropertyName("amountAtomic")]
    long AmountAtomic,
    [property: JsonPropertyName("expiresAtUnixSeconds")]
    long ExpiresAtUnixSeconds);
