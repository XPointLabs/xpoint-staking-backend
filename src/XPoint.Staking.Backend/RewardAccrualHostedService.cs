using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.Options;

namespace XPoint.Staking.Backend;

public sealed class RewardAccrualHostedService : BackgroundService
{
    private static readonly string RewardRateSelector =
        Epoche.Keccak256.ComputeEthereumFunctionSelector("rewardRate()", true);

    private readonly EventIndexer _indexer;
    private readonly EthereumJsonRpcClient _rpc;
    private readonly ServiceNodeObligationService _obligations;
    private readonly IOptionsMonitor<BackendContractOptions> _options;
    private readonly ILogger<RewardAccrualHostedService> _logger;

    public RewardAccrualHostedService(
        EventIndexer indexer,
        EthereumJsonRpcClient rpc,
        ServiceNodeObligationService obligations,
        IOptionsMonitor<BackendContractOptions> options,
        ILogger<RewardAccrualHostedService> logger)
    {
        _indexer = indexer;
        _rpc = rpc;
        _obligations = obligations;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delaySeconds = Math.Max(1, _options.CurrentValue.RewardAccrualIntervalSeconds);
            try
            {
                await AccrueOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reward accrual tick failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task AccrueOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.EthereumRpcUrl)
            || string.IsNullOrWhiteSpace(options.RewardRatePoolAddress)
            || options.RewardAccrualIntervalSeconds <= 0)
        {
            return;
        }

        var rewardRateHex = await _rpc.EthCallAsync(
            options.EthereumRpcUrl,
            options.EthereumFallbackRpcUrls,
            options.RewardRatePoolAddress,
            RewardRateSelector,
            cancellationToken).ConfigureAwait(false);
        var rewardRateAtomic = HexUInt256ToInt64(rewardRateHex);
        var rewardEligibleNodeIds = await _obligations.GetRewardEligibleNodeIdsAsync(cancellationToken)
            .ConfigureAwait(false);
        var accrued = _indexer.ApplyRewardAccrual(rewardRateAtomic, DateTimeOffset.UtcNow, rewardEligibleNodeIds);
        if (accrued > 0)
        {
            _logger.LogInformation(
                "Accrued {AccruedAtomic} atomic XPNT from reward rate {RewardRateAtomic}.",
                accrued,
                rewardRateAtomic);
        }
    }

    private static long HexUInt256ToInt64(string hex)
    {
        var normalized = EthereumJsonRpcClient.Strip0x(hex);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return 0;
        }

        var value = BigInteger.Parse("0" + normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (value > long.MaxValue)
        {
            throw new OverflowException("Reward rate does not fit into a signed 64-bit integer.");
        }

        return (long)value;
    }
}
