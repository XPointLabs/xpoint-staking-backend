using System.Text;
using System.Text.Json;

namespace XPoint.Staking.Backend;

public sealed class EthereumJsonRpcClient
{
    private const int MaxAttemptsPerEndpoint = 2;

    private readonly HttpClient _httpClient;

    public EthereumJsonRpcClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> EthCallAsync(
        string rpcUrl,
        string fallbackRpcUrls,
        string to,
        string data,
        CancellationToken cancellationToken) =>
        await EthCallAsync(
            BuildRpcUrls(rpcUrl, fallbackRpcUrls),
            to,
            data,
            cancellationToken).ConfigureAwait(false);

    public async Task<string> EthCallAsync(
        string rpcUrl,
        string to,
        string data,
        CancellationToken cancellationToken)
    {
        return await EthCallAsync(
            BuildRpcUrls(rpcUrl, ""),
            to,
            data,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> EthCallAsync(
        IReadOnlyList<string> rpcUrls,
        string to,
        string data,
        CancellationToken cancellationToken)
    {
        if (rpcUrls.Count == 0)
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

        Exception? lastTransient = null;
        foreach (var rpcUrl in rpcUrls)
        {
            for (var attempt = 1; attempt <= MaxAttemptsPerEndpoint; attempt++)
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(rpcUrl, content, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var exception = new HttpRequestException(
                        $"eth_call HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {TrimBody(body)}",
                        null,
                        response.StatusCode);
                    if (IsTransient(response.StatusCode))
                    {
                        lastTransient = exception;
                        if (attempt < MaxAttemptsPerEndpoint)
                        {
                            await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        break;
                    }

                    throw exception;
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    if (IsTransientJsonRpcError(error))
                    {
                        lastTransient = new InvalidOperationException($"eth_call failed: {error}");
                        if (attempt < MaxAttemptsPerEndpoint)
                        {
                            await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        break;
                    }

                    throw new InvalidOperationException($"eth_call failed: {error}");
                }

                return doc.RootElement.GetProperty("result").GetString()
                    ?? throw new InvalidOperationException("eth_call response did not include result.");
            }
        }

        throw new InvalidOperationException("All configured Arbitrum RPC endpoints failed.", lastTransient);
    }

    public async Task<JsonRpcForwardResult> ForwardJsonRpcAsync(
        string rpcUrl,
        string fallbackRpcUrls,
        string payload,
        CancellationToken cancellationToken)
    {
        var rpcUrls = BuildRpcUrls(rpcUrl, fallbackRpcUrls);
        if (rpcUrls.Count == 0)
        {
            throw new InvalidOperationException("Contracts:EthereumRpcUrl is required.");
        }

        Exception? lastTransient = null;
        foreach (var endpoint in rpcUrls)
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            if (!response.IsSuccessStatusCode)
            {
                var exception = new HttpRequestException(
                    $"JSON-RPC HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {TrimBody(body)}",
                    null,
                    response.StatusCode);
                if (IsTransient(response.StatusCode))
                {
                    lastTransient = exception;
                    continue;
                }

                return new JsonRpcForwardResult((int)response.StatusCode, contentType, body);
            }

            if (HasTransientJsonRpcError(body))
            {
                lastTransient = new InvalidOperationException("JSON-RPC endpoint returned a transient error.");
                continue;
            }

            return new JsonRpcForwardResult((int)response.StatusCode, contentType, body);
        }

        throw new InvalidOperationException("All configured Arbitrum RPC endpoints failed.", lastTransient);
    }

    public static IReadOnlyList<string> BuildRpcUrls(string primaryRpcUrl, string? fallbackRpcUrls)
    {
        var urls = new List<string>();
        AddIfPresent(primaryRpcUrl);
        foreach (var item in (fallbackRpcUrls ?? "").Split(
            new[] { ',', ';', '\r', '\n', '\t', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddIfPresent(item);
        }

        return urls;

        void AddIfPresent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim();
            if (!urls.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(normalized);
            }
        }
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

    public async Task<string> EthChainIdAsync(string rpcUrl, string fallbackRpcUrls, CancellationToken cancellationToken) =>
        await SendForResultAsync(rpcUrl, fallbackRpcUrls, "eth_chainId", Array.Empty<object>(), cancellationToken).ConfigureAwait(false);

    public async Task<string> EthGetCodeAsync(string rpcUrl, string fallbackRpcUrls, string address, CancellationToken cancellationToken) =>
        await SendForResultAsync(rpcUrl, fallbackRpcUrls, "eth_getCode", new object[] { NormalizeAddress(address), "latest" }, cancellationToken).ConfigureAwait(false);

    private async Task<string> SendForResultAsync(
        string rpcUrl,
        string fallbackRpcUrls,
        string method,
        object[] parameters,
        CancellationToken cancellationToken)
    {
        var endpoints = BuildRpcUrls(rpcUrl, fallbackRpcUrls);
        if (endpoints.Count == 0)
        {
            throw new InvalidOperationException("Contracts:EthereumRpcUrl is required.");
        }

        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
        Exception? lastFailure = null;
        foreach (var endpoint in endpoints)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lastFailure = new HttpRequestException($"{method} returned HTTP {(int)response.StatusCode}.");
                    continue;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (document.RootElement.TryGetProperty("error", out _)
                    || !document.RootElement.TryGetProperty("result", out var result)
                    || result.ValueKind != JsonValueKind.String)
                {
                    lastFailure = new InvalidOperationException($"{method} did not return a result.");
                    continue;
                }
                return result.GetString()!;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                lastFailure = ex;
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        throw new InvalidOperationException($"All configured RPC endpoints failed {method}.", lastFailure);
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == System.Net.HttpStatusCode.TooManyRequests
            || statusCode == System.Net.HttpStatusCode.RequestTimeout
            || code >= 500;
    }

    private static bool IsTransientJsonRpcError(JsonElement error)
    {
        if (error.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var numericCode)
            && (numericCode == 429 || numericCode == -32005))
        {
            return true;
        }

        var message = error.ToString();
        return message.Contains("rate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too many", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("temporar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasTransientJsonRpcError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && IsTransientJsonRpcError(error);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, attempt)));

    private static string TrimBody(string body) =>
        body.Length <= 512 ? body : body[..512] + "...";
}

public sealed record JsonRpcForwardResult(int StatusCode, string ContentType, string Body);
