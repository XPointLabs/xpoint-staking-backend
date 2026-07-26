using System.Globalization;
using Microsoft.Extensions.Options;

namespace XPoint.Staking.Backend;

public sealed class StakingReadinessProbe
{
    private readonly BackendContractOptions _options;
    private readonly EthereumJsonRpcClient _rpc;

    public StakingReadinessProbe(IOptions<BackendContractOptions> options, EthereumJsonRpcClient rpc)
    {
        _options = options.Value;
        _rpc = rpc;
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (!TryGetContractAddresses(out var addresses) || string.IsNullOrWhiteSpace(_options.EthereumRpcUrl))
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.ReadinessTimeoutSeconds, 1, 30)));
        try
        {
            var chain = await _rpc.EthChainIdAsync(_options.EthereumRpcUrl, _options.EthereumFallbackRpcUrls, timeout.Token).ConfigureAwait(false);
            if (!TryParseChainId(chain, out var chainId) || chainId != _options.ChainId)
            {
                return false;
            }

            foreach (var address in addresses)
            {
                var code = await _rpc.EthGetCodeAsync(_options.EthereumRpcUrl, _options.EthereumFallbackRpcUrls, address, timeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(code) || string.Equals(code, "0x", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "0x0", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private bool TryGetContractAddresses(out string[] addresses)
    {
        try
        {
            addresses =
            [
                EthereumJsonRpcClient.NormalizeAddress(_options.TokenAddress),
                EthereumJsonRpcClient.NormalizeAddress(_options.ServiceNodeRewardsAddress),
                EthereumJsonRpcClient.NormalizeAddress(_options.RewardRatePoolAddress),
                EthereumJsonRpcClient.NormalizeAddress(_options.ServiceNodeContributionFactoryAddress)
            ];
            return true;
        }
        catch (InvalidOperationException)
        {
            addresses = [];
            return false;
        }
    }

    private static bool TryParseChainId(string value, out long chainId)
    {
        var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return long.TryParse(normalized, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out chainId);
    }
}
