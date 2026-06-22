using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace XPoint.Staking.Backend;

public sealed class PriceFeedService
{
    private const string Slot0Selector = "0x3850c7bd";
    private const string Token0Selector = "0x0dfe1681";
    private const string Token1Selector = "0xd21220a7";
    private const string DecimalsSelector = "0x313ce567";

    private readonly HttpClient _httpClient;
    private readonly IOptions<BackendPriceOptions> _options;
    private readonly ILogger<PriceFeedService> _logger;

    public PriceFeedService(
        HttpClient httpClient,
        IOptions<BackendPriceOptions> options,
        ILogger<PriceFeedService> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<CurrentPriceDto> GetCurrentPriceAsync(
        string? token,
        CancellationToken cancellationToken)
    {
        var options = RequireConfiguredOptions();
        var snapshot = await ReadUniswapPoolSnapshotAsync(options, cancellationToken).ConfigureAwait(false);
        var requestedToken = string.IsNullOrWhiteSpace(token)
            ? options.DefaultToken
            : token.Trim().ToLowerInvariant();
        var currency = NormalizeCurrency(options.VsCurrency);

        return new CurrentPriceDto
        {
            Token = requestedToken,
            ProviderToken = snapshot.PoolAddress,
            Currency = currency,
            Price = snapshot.Price,
            MarketCap = null,
            Change24h = null,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    public async Task<IReadOnlyList<PricePointDto>> GetPriceHistoryAsync(
        string token,
        int days,
        CancellationToken cancellationToken)
    {
        if (days <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(days), "Price history period must be positive.");
        }

        var current = await GetCurrentPriceAsync(token, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var points = new List<PricePointDto>(days + 1);
        for (var offset = days; offset >= 0; offset--)
        {
            points.Add(new PricePointDto
            {
                Price = current.Price,
                Timestamp = now - offset * 86_400L
            });
        }

        if (points.Count == 0)
        {
            _logger.LogWarning("Uniswap price provider returned no history points for token {Token}.", token);
        }

        return points;
    }

    private async Task<UniswapPriceSnapshot> ReadUniswapPoolSnapshotAsync(
        BackendPriceOptions options,
        CancellationToken cancellationToken)
    {
        var poolAddress = NormalizeAddress(options.UniswapPoolAddress);
        var baseTokenAddress = NormalizeAddress(options.BaseTokenAddress);
        var quoteTokenAddress = NormalizeAddress(options.QuoteTokenAddress);

        var token0 = DecodeAddress(await EthCallAsync(
            options.EthereumRpcUrl,
            poolAddress,
            Token0Selector,
            cancellationToken).ConfigureAwait(false));
        var token1 = DecodeAddress(await EthCallAsync(
            options.EthereumRpcUrl,
            poolAddress,
            Token1Selector,
            cancellationToken).ConfigureAwait(false));
        var slot0 = await EthCallAsync(
            options.EthereumRpcUrl,
            poolAddress,
            Slot0Selector,
            cancellationToken).ConfigureAwait(false);
        var decimals0 = await ReadDecimalsAsync(options.EthereumRpcUrl, token0, cancellationToken).ConfigureAwait(false);
        var decimals1 = await ReadDecimalsAsync(options.EthereumRpcUrl, token1, cancellationToken).ConfigureAwait(false);

        var sqrtPriceX96 = ReadUInt256Word(slot0);
        if (sqrtPriceX96 <= BigInteger.Zero)
        {
            throw new PriceFeedUnavailableException("Uniswap pool slot0 returned an invalid sqrt price.");
        }

        var token1PerToken0 = CalculateToken1PerToken0(sqrtPriceX96, decimals0, decimals1);
        var price = ResolveBaseTokenPrice(
            token0,
            token1,
            baseTokenAddress,
            quoteTokenAddress,
            token1PerToken0);

        if (!double.IsFinite(price) || price <= 0)
        {
            throw new PriceFeedUnavailableException("Uniswap pool returned an invalid XPNT price.");
        }

        return new UniswapPriceSnapshot(poolAddress, price);
    }

    private async Task<int> ReadDecimalsAsync(
        string rpcUrl,
        string tokenAddress,
        CancellationToken cancellationToken)
    {
        var result = await EthCallAsync(
            rpcUrl,
            tokenAddress,
            DecimalsSelector,
            cancellationToken).ConfigureAwait(false);
        var decimals = ReadUInt256Word(result);
        if (decimals < 0 || decimals > 36)
        {
            throw new PriceFeedUnavailableException($"Token {tokenAddress} returned unsupported decimals {decimals}.");
        }

        return (int)decimals;
    }

    private async Task<string> EthCallAsync(
        string rpcUrl,
        string to,
        string data,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "eth_call",
            @params = new object[]
            {
                new { to = NormalizeAddress(to), data },
                "latest"
            }
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(rpcUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PriceFeedUnavailableException(
                $"Arbitrum RPC returned HTTP {(int)response.StatusCode}: {TrimBody(body)}");
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            throw new PriceFeedUnavailableException($"Arbitrum RPC eth_call failed: {error}");
        }

        var result = document.RootElement.GetProperty("result").GetString();
        if (string.IsNullOrWhiteSpace(result) || result == "0x")
        {
            throw new PriceFeedUnavailableException("Arbitrum RPC eth_call returned no data.");
        }

        return result;
    }

    private BackendPriceOptions RequireConfiguredOptions()
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.EthereumRpcUrl))
        {
            throw new PriceFeedUnavailableException(
                "Price provider is not configured. Set Price:EthereumRpcUrl.");
        }

        if (string.IsNullOrWhiteSpace(options.UniswapPoolAddress))
        {
            throw new PriceFeedUnavailableException(
                "Price provider is not configured. Set Price:UniswapPoolAddress.");
        }

        if (string.IsNullOrWhiteSpace(options.BaseTokenAddress)
            || string.IsNullOrWhiteSpace(options.QuoteTokenAddress))
        {
            throw new PriceFeedUnavailableException(
                "Price provider is not configured. Set Price:BaseTokenAddress and Price:QuoteTokenAddress.");
        }

        return options;
    }

    private static double CalculateToken1PerToken0(
        BigInteger sqrtPriceX96,
        int decimals0,
        int decimals1)
    {
        var ratio = (double)(sqrtPriceX96 * sqrtPriceX96) / Math.Pow(2, 192);
        return ratio * Math.Pow(10, decimals0 - decimals1);
    }

    private static double ResolveBaseTokenPrice(
        string token0,
        string token1,
        string baseToken,
        string quoteToken,
        double token1PerToken0)
    {
        if (string.Equals(baseToken, token0, StringComparison.OrdinalIgnoreCase)
            && string.Equals(quoteToken, token1, StringComparison.OrdinalIgnoreCase))
        {
            return token1PerToken0;
        }

        if (string.Equals(baseToken, token1, StringComparison.OrdinalIgnoreCase)
            && string.Equals(quoteToken, token0, StringComparison.OrdinalIgnoreCase))
        {
            return 1 / token1PerToken0;
        }

        throw new PriceFeedUnavailableException(
            $"Configured base/quote tokens do not match Uniswap pool tokens {token0}/{token1}.");
    }

    private static string DecodeAddress(string value)
    {
        var bytes = HexToBytes(value);
        if (bytes.Length < 32)
        {
            throw new PriceFeedUnavailableException("ABI address response was shorter than 32 bytes.");
        }

        return "0x" + Convert.ToHexString(bytes.AsSpan(12, 20)).ToLowerInvariant();
    }

    private static BigInteger ReadUInt256Word(string value)
    {
        var bytes = HexToBytes(value);
        if (bytes.Length < 32)
        {
            throw new PriceFeedUnavailableException("ABI uint256 response was shorter than 32 bytes.");
        }

        return new BigInteger(bytes.AsSpan(0, 32), isUnsigned: true, isBigEndian: true);
    }

    private static string NormalizeAddress(string address)
    {
        var normalized = Strip0x(address).ToLowerInvariant();
        if (normalized.Length != 40 || !normalized.All(Uri.IsHexDigit))
        {
            throw new PriceFeedUnavailableException("Expected 20-byte Ethereum address.");
        }

        return "0x" + normalized;
    }

    private static byte[] HexToBytes(string hex)
    {
        var normalized = Strip0x(hex);
        if (normalized.Length % 2 == 1)
        {
            normalized = "0" + normalized;
        }

        return Convert.FromHexString(normalized);
    }

    private static string Strip0x(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

    private static string NormalizeCurrency(string currency)
    {
        return string.IsNullOrWhiteSpace(currency)
            ? "usd"
            : currency.Trim().ToLowerInvariant();
    }

    private static string TrimBody(string body)
    {
        return body.Length <= 240 ? body : body[..240] + "...";
    }

    private sealed record UniswapPriceSnapshot(
        string PoolAddress,
        double Price);
}

public sealed class PriceFeedUnavailableException : InvalidOperationException
{
    public PriceFeedUnavailableException(string message)
        : base(message)
    {
    }
}

public sealed record CurrentPriceDto
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = "";

    [JsonPropertyName("provider_token")]
    public string ProviderToken { get; init; } = "";

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "usd";

    [JsonPropertyName("price")]
    public double Price { get; init; }

    [JsonPropertyName("market_cap")]
    public double? MarketCap { get; init; }

    [JsonPropertyName("change_24h")]
    public double? Change24h { get; init; }

    [JsonPropertyName("t")]
    public long Timestamp { get; init; }
}

public sealed record PricePointDto
{
    [JsonPropertyName("price")]
    public double Price { get; init; }

    [JsonPropertyName("t")]
    public long Timestamp { get; init; }
}
