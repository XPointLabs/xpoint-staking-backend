using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Neo.Cryptography.BLS12_381;

namespace XPoint.Staking.Backend;

public sealed class NetworkBlsRewardSignatureService
{
    private const int G2EipBytes = 256;

    private readonly EventIndexer _indexer;
    private readonly RewardStateService _rewards;
    private readonly RegistryRegistrationClient _registry;
    private readonly ServiceNodeObligationService _obligations;
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<BackendContractOptions> _options;
    private readonly ILogger<NetworkBlsRewardSignatureService> _logger;

    public NetworkBlsRewardSignatureService(
        EventIndexer indexer,
        RewardStateService rewards,
        RegistryRegistrationClient registry,
        ServiceNodeObligationService obligations,
        HttpClient httpClient,
        IOptionsMonitor<BackendContractOptions> options,
        ILogger<NetworkBlsRewardSignatureService> logger)
    {
        _indexer = indexer;
        _rewards = rewards;
        _registry = registry;
        _obligations = obligations;
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<RewardsSignatureDto> CreateRewardsSignatureAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var normalizedAddress = EthereumJsonRpcClient.NormalizeAddress(address);
        var quote = await _rewards.CreateRewardSigningQuoteAsync(normalizedAddress, cancellationToken)
            .ConfigureAwait(false);
        var signature = await CreateNetworkSignatureAsync(
            _options.CurrentValue,
            new NodeQuorumSignatureRequest("reward", normalizedAddress, quote.AmountAtomic, quote.QuoteId, "", 0),
            cancellationToken).ConfigureAwait(false);
        var network = _indexer.GetNetworkInfo();
        return new RewardsSignatureDto
        {
            AggregatePublicKey = _indexer.GetAggregatePublicKeyHint(),
            Amount = quote.AmountAtomic,
            Height = network.BlockHeight,
            MessageToSign = signature.MessageToSign,
            NonSignerIndices = signature.NonSignerIndices,
            Signature = signature.Signature
        };
    }

    public async Task<BlsExitSignatureDto> CreateExitSignatureAsync(
        string blsPublicKey,
        bool liquidate,
        CancellationToken cancellationToken)
    {
        var normalizedPublicKey = NormalizeHex(blsPublicKey, 128);
        var activeBlsKeys = _indexer.GetAddedBlsKeys();
        if (!activeBlsKeys.ContainsKey(normalizedPublicKey))
        {
            throw new InvalidOperationException("BLS public key is not active on the service node rewards contract.");
        }

        var obligation = await _obligations.GetStatusByBlsPublicKeyAsync(normalizedPublicKey, cancellationToken)
            .ConfigureAwait(false);
        if (liquidate && !obligation.LiquidationSignatureEligible)
        {
            throw new ServiceNodeSignatureNotEligibleException(
                $"BLS public key is not eligible for liquidation signature: {obligation.Status} ({obligation.Reason})");
        }

        if (!liquidate && !obligation.ExitSignatureEligible)
        {
            throw new ServiceNodeSignatureNotEligibleException(
                $"BLS public key is not eligible for exit signature: {obligation.Status} ({obligation.Reason})");
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = await CreateNetworkSignatureAsync(
            _options.CurrentValue,
            new NodeQuorumSignatureRequest(
                liquidate ? "liquidate" : "exit",
                "",
                0,
                "",
                normalizedPublicKey,
                timestamp),
            cancellationToken).ConfigureAwait(false);

        return new BlsExitSignatureDto
        {
            BlsPublicKey = normalizedPublicKey,
            MessageToSign = signature.MessageToSign,
            NonSignerIndices = signature.NonSignerIndices,
            Signature = signature.Signature,
            Timestamp = timestamp
        };
    }

    private async Task<QuorumAggregateSignature> CreateNetworkSignatureAsync(
        BackendContractOptions options,
        NodeQuorumSignatureRequest request,
        CancellationToken cancellationToken)
    {
        var activeBlsKeys = _indexer.GetAddedBlsKeys();
        if (activeBlsKeys.Count == 0)
        {
            throw new InvalidOperationException("No active service node BLS keys are available for quorum signing.");
        }

        var signingNodes = await _registry.GetSigningNodesAsync(cancellationToken).ConfigureAwait(false);
        var activeSigningNodes = new List<RegistrySigningNode>();
        foreach (var node in signingNodes)
        {
            var publicKey = NormalizeHex(node.BlsPublicKey);
            if (activeBlsKeys.ContainsKey(publicKey))
            {
                activeSigningNodes.Add(node with { BlsPublicKey = publicKey });
            }
        }

        if (activeSigningNodes.Count == 0)
        {
            throw new InvalidOperationException("No registry-published signing endpoints matched active service node BLS keys.");
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.QuorumSignatureTimeoutSeconds));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var requests = activeSigningNodes
            .Select(node => RequestSignatureAsync(node, request, timeoutCts.Token))
            .ToArray();
        var responses = await Task.WhenAll(requests).ConfigureAwait(false);
        var validSignatures = new List<NodeQuorumSignatureResponse>();
        var signedNodeIds = new HashSet<long>();
        string? messageToSign = null;

        foreach (var response in responses.OfType<NodeQuorumSignatureResponse>())
        {
            var publicKey = NormalizeHex(response.BlsPublicKey);
            if (!activeBlsKeys.TryGetValue(publicKey, out var nodeId))
            {
                _logger.LogWarning("Ignoring quorum signature from unknown BLS public key {PublicKey}.", publicKey);
                continue;
            }

            if (!ResponseMatchesRequest(response, request))
            {
                continue;
            }

            if (messageToSign is null)
            {
                messageToSign = NormalizeHex(response.MessageToSign);
            }
            else if (!string.Equals(messageToSign, NormalizeHex(response.MessageToSign), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Ignoring quorum signature from node {NodeId}: msg_to_sign mismatch.", nodeId);
                continue;
            }

            validSignatures.Add(response with
            {
                BlsPublicKey = publicKey,
                MessageToSign = messageToSign,
                Signature = NormalizeHex(response.Signature)
            });
            signedNodeIds.Add(nodeId);
        }

        if (validSignatures.Count == 0)
        {
            throw new InvalidOperationException($"No service nodes returned a valid {request.Type} quorum signature.");
        }

        var nonSignerIndices = activeBlsKeys.Values
            .Where(nodeId => !signedNodeIds.Contains(nodeId))
            .Order()
            .ToArray();
        var maxNonSigners = await GetMaxPermittedNonSignersAsync(
            options,
            activeBlsKeys.Count,
            cancellationToken).ConfigureAwait(false);
        if (nonSignerIndices.Length > maxNonSigners)
        {
            throw new InvalidOperationException(
                $"Insufficient quorum signatures: {validSignatures.Count}/{activeBlsKeys.Count} active nodes signed; {nonSignerIndices.Length} non-signers exceeds threshold {maxNonSigners}.");
        }

        return new QuorumAggregateSignature(
            messageToSign ?? "",
            nonSignerIndices,
            AggregateSignatures(validSignatures.Select(item => item.Signature)));
    }

    private async Task<int> GetMaxPermittedNonSignersAsync(
        BackendContractOptions options,
        int activeNodeCount,
        CancellationToken cancellationToken)
    {
        if (options.QuorumNonSignerThresholdMax <= 0)
        {
            throw new InvalidOperationException("Contracts:QuorumNonSignerThresholdMax must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.EthereumRpcUrl)
            || string.IsNullOrWhiteSpace(options.ServiceNodeRewardsAddress))
        {
            return CalculateMaxPermittedNonSigners(activeNodeCount, options.QuorumNonSignerThresholdMax);
        }

        var selector = Epoche.Keccak256.ComputeEthereumFunctionSelector("blsNonSignerThreshold()", true);
        var result = await EthCallAsync(
            options.EthereumRpcUrl,
            options.EthereumFallbackRpcUrls,
            options.ServiceNodeRewardsAddress,
            selector,
            cancellationToken).ConfigureAwait(false);
        var contractThreshold = DecodeUInt256AsInt(result);
        var localThreshold = CalculateMaxPermittedNonSigners(activeNodeCount, options.QuorumNonSignerThresholdMax);
        if (contractThreshold > localThreshold)
        {
            _logger.LogWarning(
                "Contract blsNonSignerThreshold {ContractThreshold} exceeds locally calculated threshold {LocalThreshold}; enforcing the stricter local threshold.",
                contractThreshold,
                localThreshold);
            return localThreshold;
        }

        return contractThreshold;
    }

    private static int CalculateMaxPermittedNonSigners(int activeNodeCount, int configuredMax)
    {
        var oneThirdOfNodes = Math.Max(0, activeNodeCount) / 3;
        return Math.Min(oneThirdOfNodes, configuredMax);
    }

    private async Task<string> EthCallAsync(
        string primaryRpcUrl,
        string fallbackRpcUrls,
        string to,
        string data,
        CancellationToken cancellationToken)
    {
        var rpcUrls = new List<string>();
        AddIfPresent(primaryRpcUrl);
        foreach (var item in fallbackRpcUrls.Split(
            new[] { ',', ';', '\r', '\n', '\t', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddIfPresent(item);
        }

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "eth_call",
            @params = new object[]
            {
                new { to = EthereumJsonRpcClient.NormalizeAddress(to), data },
                "latest"
            }
        });

        Exception? lastException = null;
        foreach (var rpcUrl in rpcUrls)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(rpcUrl, content, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException($"Ethereum RPC eth_call failed: {error}");
                }

                var result = document.RootElement.GetProperty("result").GetString();
                if (string.IsNullOrWhiteSpace(result))
                {
                    throw new InvalidOperationException("Ethereum RPC eth_call returned an empty result.");
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
            }
        }

        throw new InvalidOperationException("Ethereum RPC eth_call failed while reading blsNonSignerThreshold().", lastException);

        void AddIfPresent(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && !rpcUrls.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                rpcUrls.Add(value.Trim());
            }
        }
    }

    private static int DecodeUInt256AsInt(string value)
    {
        var normalized = NormalizeHex(value);
        if (normalized.Length < 64)
        {
            throw new InvalidOperationException("Ethereum uint256 result was shorter than 32 bytes.");
        }

        var parsed = System.Numerics.BigInteger.Parse(
            "0" + normalized[..64],
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        if (parsed > int.MaxValue)
        {
            throw new InvalidOperationException("Ethereum uint256 result exceeds Int32.MaxValue.");
        }

        return (int)parsed;
    }

    private async Task<NodeQuorumSignatureResponse?> RequestSignatureAsync(
        RegistrySigningNode node,
        NodeQuorumSignatureRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                node.SigningEndpoint,
                request,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Quorum signer endpoint {Endpoint} for node {NodeId} returned {StatusCode}: {Body}",
                    node.SigningEndpoint,
                    node.NodeId,
                    (int)response.StatusCode,
                    body);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<NodeQuorumSignatureResponse>(cancellationToken)
                .ConfigureAwait(false);
            if (result is null)
            {
                return null;
            }

            var responseKey = NormalizeHex(result.BlsPublicKey);
            if (!string.Equals(responseKey, node.BlsPublicKey, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Quorum signer endpoint {Endpoint} was registered for BLS key {ExpectedKey} but responded as {ActualKey}.",
                    node.SigningEndpoint,
                    node.BlsPublicKey,
                    responseKey);
                return null;
            }

            return result with { BlsPublicKey = responseKey };
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Quorum signer endpoint {Endpoint} timed out or was cancelled.", node.SigningEndpoint);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quorum signer endpoint {Endpoint} failed.", node.SigningEndpoint);
            return null;
        }
    }

    private bool ResponseMatchesRequest(NodeQuorumSignatureResponse response, NodeQuorumSignatureRequest request)
    {
        string responseType;
        try
        {
            responseType = string.IsNullOrWhiteSpace(response.Type)
                ? request.Type
                : NormalizeMessageType(response.Type);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Ignoring quorum signature with unsupported response type {Type}.", response.Type);
            return false;
        }

        if (!string.Equals(responseType, request.Type, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Ignoring quorum signature response of type {ResponseType}; expected {RequestType}.",
                responseType,
                request.Type);
            return false;
        }

        if (string.Equals(request.Type, "reward", StringComparison.OrdinalIgnoreCase)
            && response.Amount != request.Amount)
        {
            _logger.LogWarning(
                "Ignoring reward quorum signature: amount {ResponseAmount} did not match {Amount}.",
                response.Amount,
                request.Amount);
            return false;
        }

        if (!string.Equals(request.Type, "reward", StringComparison.OrdinalIgnoreCase)
            && response.Timestamp != request.Timestamp)
        {
            _logger.LogWarning(
                "Ignoring {Type} quorum signature: timestamp {ResponseTimestamp} did not match {Timestamp}.",
                request.Type,
                response.Timestamp,
                request.Timestamp);
            return false;
        }

        return true;
    }

    private static string AggregateSignatures(IEnumerable<string> signatures)
    {
        G2Projective? aggregate = null;
        foreach (var signature in signatures)
        {
            var point = G2Affine.FromUncompressed(EipG2ToNeo(HexToBytes(signature)));
            var projective = point * Scalar.One;
            aggregate = aggregate is null ? projective : aggregate.Value + projective;
        }

        var aggregatePoint = aggregate
            ?? throw new InvalidOperationException("No signatures were available for aggregation.");
        var affine = ToAffine(aggregatePoint);
        return Hex(NeoG2ToEip(affine.ToUncompressed()));
    }

    private static G2Affine ToAffine(G2Projective point)
    {
        var source = new[] { point };
        var target = new G2Affine[1];
        G2Projective.BatchNormalize(source, target);
        return target[0];
    }

    private static byte[] NeoG2ToEip(byte[] neo)
    {
        if (neo.Length != 192)
        {
            throw new ArgumentException("BLS12-381 G2 uncompressed point must be 192 bytes.", nameof(neo));
        }

        var parts = Split(neo, 48);
        return Concat(Pad48To64(parts[1]), Pad48To64(parts[0]), Pad48To64(parts[3]), Pad48To64(parts[2]));
    }

    private static byte[] EipG2ToNeo(byte[] eip)
    {
        if (eip.Length != G2EipBytes)
        {
            throw new ArgumentException("EIP-2537 G2 point must be 256 bytes.", nameof(eip));
        }

        var parts = Split(eip, 64).Select(Trim64To48).ToArray();
        return Concat(parts[1], parts[0], parts[3], parts[2]);
    }

    private static byte[][] Split(byte[] bytes, int size)
    {
        if (bytes.Length % size != 0)
        {
            throw new ArgumentException("Input length is not divisible by chunk size.", nameof(bytes));
        }

        var result = new byte[bytes.Length / size][];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = bytes.AsSpan(i * size, size).ToArray();
        }

        return result;
    }

    private static byte[] Pad48To64(ReadOnlySpan<byte> value)
    {
        if (value.Length != 48)
        {
            throw new ArgumentException("BLS12-381 field element must be 48 bytes.", nameof(value));
        }

        var result = new byte[64];
        value.CopyTo(result.AsSpan(16));
        return result;
    }

    private static byte[] Trim64To48(byte[] value)
    {
        if (value.Length != 64)
        {
            throw new ArgumentException("EIP-2537 field element must be 64 bytes.", nameof(value));
        }

        for (var i = 0; i < 16; i++)
        {
            if (value[i] != 0)
            {
                throw new ArgumentException("EIP-2537 field element has non-zero padding.", nameof(value));
            }
        }

        return value.AsSpan(16).ToArray();
    }

    private static byte[] HexToBytes(string hex)
    {
        var normalized = NormalizeHex(hex);
        if (normalized.Length % 2 == 1)
        {
            normalized = "0" + normalized;
        }

        return Convert.FromHexString(normalized);
    }

    private static string NormalizeHex(string value)
    {
        var normalized = EthereumJsonRpcClient.Strip0x(value).ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length % 2 != 0 || !normalized.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Expected non-empty hex value.");
        }

        return normalized;
    }

    private static string NormalizeHex(string value, int expectedBytes)
    {
        var normalized = NormalizeHex(value);
        if (normalized.Length != expectedBytes * 2)
        {
            throw new InvalidOperationException($"Expected {expectedBytes}-byte hex value.");
        }

        return normalized;
    }

    private static string NormalizeMessageType(string? type)
    {
        var normalized = (type ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "reward" or "rewards" => "reward",
            "exit" => "exit",
            "liquidate" or "liquidation" => "liquidate",
            _ => throw new InvalidOperationException($"Unsupported quorum signing message type '{type}'.")
        };
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static byte[] Concat(params byte[][] chunks)
    {
        var result = new byte[chunks.Sum(static item => item.Length)];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }

    private sealed record QuorumAggregateSignature(
        string MessageToSign,
        IReadOnlyList<long> NonSignerIndices,
        string Signature);

    private sealed record NodeQuorumSignatureRequest(
        [property: JsonPropertyName("type")]
        string Type,
        [property: JsonPropertyName("recipientAddress")]
        string RecipientAddress,
        [property: JsonPropertyName("amount")]
        long Amount,
        [property: JsonPropertyName("policyQuote")]
        string PolicyQuote,
        [property: JsonPropertyName("blsPublicKey")]
        string BlsPublicKey,
        [property: JsonPropertyName("timestamp")]
        long Timestamp);

    private sealed record NodeQuorumSignatureResponse
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "";

        [JsonPropertyName("nodeId")]
        public string NodeId { get; init; } = "";

        [JsonPropertyName("blsPublicKey")]
        public string BlsPublicKey { get; init; } = "";

        [JsonPropertyName("amount")]
        public long Amount { get; init; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; init; }

        [JsonPropertyName("msgToSign")]
        public string MessageToSign { get; init; } = "";

        [JsonPropertyName("signature")]
        public string Signature { get; init; } = "";
    }
}
