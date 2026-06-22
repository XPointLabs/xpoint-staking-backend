using System.Text;
using System.Text.Json;

namespace XPoint.Staking.Backend;

public sealed class EthereumJsonRpcClient
{
    private readonly HttpClient _httpClient;

    public EthereumJsonRpcClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> EthCallAsync(
        string rpcUrl,
        string to,
        string data,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rpcUrl))
        {
            throw new InvalidOperationException("Contracts:EthereumRpcUrl is required.");
        }

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
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"eth_call failed: {error}");
        }

        return doc.RootElement.GetProperty("result").GetString()
            ?? throw new InvalidOperationException("eth_call response did not include result.");
    }

    public static string NormalizeAddress(string address)
    {
        var normalized = Strip0x(address);
        if (normalized.Length != 40 || !normalized.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Expected 20-byte Ethereum address.");
        }

        return "0x" + normalized.ToLowerInvariant();
    }

    public static string Strip0x(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
}
