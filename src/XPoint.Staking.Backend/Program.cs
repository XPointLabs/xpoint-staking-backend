using System.Text;
using XPoint.Staking.Backend;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BackendContractOptions>(builder.Configuration.GetSection("Contracts"));
builder.Services.Configure<BackendRegistryOptions>(builder.Configuration.GetSection("Registry"));
builder.Services.Configure<BackendPriceOptions>(builder.Configuration.GetSection("Price"));
builder.Services.AddSingleton<EventIndexer>();
builder.Services.AddHttpClient<EthereumJsonRpcClient>();
builder.Services.AddHttpClient<PriceFeedService>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackendPriceOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
    if (!string.IsNullOrWhiteSpace(options.BaseUrl))
    {
        client.BaseAddress = new Uri(options.BaseUrl.EndsWith('/')
            ? options.BaseUrl
            : options.BaseUrl + "/");
    }
});
builder.Services.AddSingleton<RewardStateService>();
builder.Services.AddSingleton<ServiceNodeObligationService>();
builder.Services.AddHttpClient<NetworkBlsRewardSignatureService>();
builder.Services.AddHostedService<RewardAccrualHostedService>();
builder.Services.AddHttpClient<RegistryRegistrationClient>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackendRegistryOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));

    if (!string.IsNullOrWhiteSpace(options.BaseUrl))
    {
        client.BaseAddress = new Uri(options.BaseUrl.EndsWith('/')
            ? options.BaseUrl
            : options.BaseUrl + "/");
    }
});

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/info"));
app.MapGet("/health/live", () => Results.Ok(new { ok = true, service = "xpoint-staking-backend" }));

app.MapGet("/info", (IConfiguration configuration, EventIndexer indexer) => Results.Ok(new
{
    network = indexer.GetNetworkInfo(),
    t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
    token = new { name = "XPoint", symbol = "XPNT", decimals = 9 },
    contracts = ToPublicContractOptions(
        configuration.GetSection("Contracts").Get<BackendContractOptions>() ?? new BackendContractOptions())
}));

app.MapGet("/hf_info", (EventIndexer indexer) => Results.Ok(new HardForkInfoResponse
{
    Network = indexer.GetNetworkInfo()
}));

app.MapGet("/nodes", (EventIndexer indexer, string? address) => Results.Ok(new StakesResponse
{
    Network = indexer.GetNetworkInfo(),
    Stakes = indexer.GetSessionStakes(address).ToArray(),
    Contracts = string.IsNullOrWhiteSpace(address)
        ? indexer.GetContributionContracts().ToArray()
        : indexer.GetContributionContractsForWallet(address).ToArray(),
    AddedBlsKeys = indexer.GetAddedBlsKeys()
}));

app.MapGet("/nodes/{key}", (string key, EventIndexer indexer) => Results.Ok(new StakesResponse
{
    Network = indexer.GetNetworkInfo(),
    Stakes = indexer.GetSessionStakes(key).ToArray(),
    Contracts = LooksLikeEthereumAddress(key)
        ? indexer.GetContributionContractsForWallet(key).ToArray()
        : Array.Empty<ContributionContractDto>(),
    AddedBlsKeys = indexer.GetAddedBlsKeys()
}));

app.MapGet("/stakes/{key}", (string key, EventIndexer indexer) => Results.Ok(new StakesResponse
{
    Network = indexer.GetNetworkInfo(),
    Stakes = indexer.GetSessionStakes(key).ToArray(),
    Contracts = LooksLikeEthereumAddress(key)
        ? indexer.GetContributionContractsForWallet(key).ToArray()
        : Array.Empty<ContributionContractDto>(),
    AddedBlsKeys = indexer.GetAddedBlsKeys()
}));

app.MapGet("/contract_nodes", (EventIndexer indexer) => Results.Ok(new ContractNodesResponse
{
    Network = indexer.GetNetworkInfo(),
    Nodes = indexer.GetContractNodes()
}));

app.MapGet("/nodes/bls", (EventIndexer indexer) => Results.Ok(new NodesBlsKeysResponse
{
    Network = indexer.GetNetworkInfo(),
    BlsKeys = indexer.GetAddedBlsKeys()
}));

app.MapGet("/obligations", async (
    EventIndexer indexer,
    ServiceNodeObligationService obligations,
    CancellationToken cancellationToken) =>
{
    var statuses = await obligations.GetStatusesAsync(cancellationToken);
    return Results.Ok(new ServiceNodeObligationsResponse
    {
        Network = indexer.GetNetworkInfo(),
        Nodes = statuses.Select(ToObligationDto).ToArray()
    });
});

app.MapGet("/exit_liquidation_list", async (
    EventIndexer indexer,
    ServiceNodeObligationService obligations,
    CancellationToken cancellationToken) =>
{
    var network = indexer.GetNetworkInfo();
    var statuses = await obligations.GetStatusesAsync(cancellationToken);
    return Results.Ok(new ExitLiquidationListResponse
    {
        Network = network,
        Result = statuses
            .Where(static status => status.ExitSignatureEligible || status.LiquidationSignatureEligible)
            .Select(status => ToExitLiquidationListItem(status, network.BlockHeight))
            .ToArray()
    });
});

app.MapGet("/contract/contribution", (EventIndexer indexer) => Results.Ok(new ContributionContractResponse
{
    Network = indexer.GetNetworkInfo(),
    Contracts = indexer.GetContributionContracts().ToArray(),
    AddedBlsKeys = indexer.GetAddedBlsKeys()
}));

app.MapGet("/contract/contribution/{key}", (string key, EventIndexer indexer) =>
{
    if (LooksLikeEthereumAddress(key))
    {
        return Results.Ok(new ContributionContractResponse
        {
            Network = indexer.GetNetworkInfo(),
            Contracts = indexer.GetContributionContractsForWallet(key).ToArray(),
            AddedBlsKeys = indexer.GetAddedBlsKeys()
        });
    }

    return Results.Ok(new ContributionContractByKeyResponse
    {
        Network = indexer.GetNetworkInfo(),
        Contract = indexer.GetContributionContractByNodePubkey(key)
    });
});

app.MapGet("/rewards/{address}", async (
    string address,
    EventIndexer indexer,
    RewardStateService rewards,
    CancellationToken cancellationToken) => Results.Ok(new RewardsResponse
{
    Network = indexer.GetNetworkInfo(),
    Rewards = await rewards.GetSessionRewardsAsync(address, cancellationToken)
}));

app.MapPost("/rewards/{address}", async (
    string address,
    EventIndexer indexer,
    NetworkBlsRewardSignatureService signer,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(new RewardsSignatureResponse
        {
            Network = indexer.GetNetworkInfo(),
            Rewards = await signer.CreateRewardsSignatureAsync(address, cancellationToken)
        });
    }
    catch (ServiceNodeSignatureNotEligibleException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/exit/{blsPublicKey}", async (
    string blsPublicKey,
    EventIndexer indexer,
    NetworkBlsRewardSignatureService signer,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(new BlsExitSignatureResponse
        {
            Network = indexer.GetNetworkInfo(),
            Result = await signer.CreateExitSignatureAsync(blsPublicKey, liquidate: false, cancellationToken)
        });
    }
    catch (ServiceNodeSignatureNotEligibleException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/liquidation/{blsPublicKey}", async (
    string blsPublicKey,
    EventIndexer indexer,
    NetworkBlsRewardSignatureService signer,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(new BlsExitSignatureResponse
        {
            Network = indexer.GetNetworkInfo(),
            Result = await signer.CreateExitSignatureAsync(blsPublicKey, liquidate: true, cancellationToken)
        });
    }
    catch (ServiceNodeSignatureNotEligibleException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/daily-rewards/{address}", (string address, EventIndexer indexer) => Results.Ok(new DailyRewardsResponse
{
    Network = indexer.GetNetworkInfo(),
    Rewards = indexer.GetDailyRewards(address)
}));

app.MapGet("/price", GetCurrentPrice);

app.MapGet("/price/{token}", GetCurrentPrice);

app.MapGet("/prices/{token}/{period}", GetPriceHistory);

app.MapGet("/registrations/{key}", async (
    string key,
    EventIndexer indexer,
    RegistryRegistrationClient registrations,
    CancellationToken cancellationToken) => Results.Ok(new RegistrationsResponse
{
    Network = indexer.GetNetworkInfo(),
    Registrations = await registrations.GetRegistrationsAsync(key, cancellationToken)
}));

app.MapPost("/rpc/arbitrum", ForwardArbitrumRpc);

var api = app.MapGroup("/api");

api.MapPost("/rpc/arbitrum", ForwardArbitrumRpc);

api.MapPost("/events", (ChainEvent request, EventIndexer indexer) =>
{
    var result = indexer.Ingest(request);
    return Results.Ok(result);
});

api.MapPost("/chain-tip", (ChainTipRequest request, EventIndexer indexer) =>
{
    indexer.UpdateChainTip(request.BlockNumber, request.BlockHash);
    return Results.Ok(indexer.GetNetworkInfo());
});

api.MapGet("/events", (string? name, EventIndexer indexer) =>
{
    return Results.Ok(indexer.GetEvents(name));
});

api.MapGet("/events/stats", (EventIndexer indexer) => Results.Ok(indexer.GetIngestionStats()));

api.MapGet("/staking/nodes", (EventIndexer indexer) => Results.Ok(indexer.GetNodes()));

api.MapGet("/staking/nodes/{nodeId}", (string nodeId, EventIndexer indexer) =>
{
    var node = indexer.GetNode(nodeId);
    return node is null ? Results.NotFound(new { error = "node not found" }) : Results.Ok(node);
});

api.MapGet("/staking/rewards/{address}", async (
    string address,
    RewardStateService rewards,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await rewards.GetRewardsAsync(address, cancellationToken));
});

api.MapGet("/staking/state/{nodeId}", (string nodeId, EventIndexer indexer) =>
{
    var node = indexer.GetNode(nodeId);
    return node is null ? Results.NotFound(new { error = "node not found" }) : Results.Ok(node);
});

app.Run();

static bool LooksLikeEthereumAddress(string value)
{
    return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && value.Length == 42;
}

static async Task<IResult> GetCurrentPrice(
    PriceFeedService prices,
    CancellationToken cancellationToken,
    string? token = null)
{
    try
    {
        return Results.Ok(new { price = await prices.GetCurrentPriceAsync(token, cancellationToken) });
    }
    catch (PriceFeedUnavailableException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

static async Task<IResult> GetPriceHistory(
    string token,
    string period,
    PriceFeedService prices,
    CancellationToken cancellationToken)
{
    if (!TryParsePricePeriod(period, out var days))
    {
        return Results.BadRequest(new { error = "Unsupported price history period. Use 1d, 7d, 30d, 90d, 180d, or 1y." });
    }

    try
    {
        return Results.Ok(new
        {
            prices = await prices.GetPriceHistoryAsync(token, days, cancellationToken)
        });
    }
    catch (PriceFeedUnavailableException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

static bool TryParsePricePeriod(string period, out int days)
{
    days = period.Trim().ToLowerInvariant() switch
    {
        "1d" => 1,
        "7d" => 7,
        "30d" => 30,
        "90d" => 90,
        "180d" => 180,
        "1y" => 365,
        _ => 0
    };
    return days > 0;
}

static async Task<IResult> ForwardArbitrumRpc(
    HttpContext context,
    Microsoft.Extensions.Options.IOptions<BackendContractOptions> options,
    EthereumJsonRpcClient rpc,
    CancellationToken cancellationToken)
{
    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
    var payload = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    if (string.IsNullOrWhiteSpace(payload))
    {
        return Results.BadRequest(new { error = "JSON-RPC request body is required." });
    }

    try
    {
        var result = await rpc.ForwardJsonRpcAsync(
            options.Value.EthereumRpcUrl,
            options.Value.EthereumFallbackRpcUrls,
            payload,
            cancellationToken).ConfigureAwait(false);

        return Results.Content(result.Body, result.ContentType, statusCode: result.StatusCode);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

static object ToPublicContractOptions(BackendContractOptions options)
{
    return new
    {
        options.ChainId,
        options.NetworkName,
        options.TokenAddress,
        options.ServiceNodeRewardsAddress,
        options.ServiceNodeContributionFactoryAddress,
        options.RewardRatePoolAddress,
        options.StakingRequirementAtomic,
        options.MaxStakers,
        options.HardFork,
        options.Version,
        options.RewardAccrualIntervalSeconds,
        options.RewardPulseSeconds,
        options.QuorumSignatureTimeoutSeconds,
        EthereumRpcUrl = string.IsNullOrWhiteSpace(options.EthereumRpcUrl) ? "" : "configured",
        EthereumFallbackRpcUrls = string.IsNullOrWhiteSpace(options.EthereumFallbackRpcUrls) ? "" : "configured",
        QuorumSignerDiscovery = "chain-active-obligation-gated-registry-endpoints"
    };
}

static ServiceNodeObligationDto ToObligationDto(ServiceNodeObligationStatus status)
{
    return new ServiceNodeObligationDto
    {
        NodeId = status.NodeId,
        ContractId = status.ContractId,
        BlsPublicKey = status.BlsPublicKey,
        ServiceNodePubkey = status.Ed25519PublicKey,
        ChainStatus = status.ChainStatus,
        Status = status.Status,
        Reason = status.Reason,
        LastHeartbeatAt = status.LastHeartbeatAt,
        HeartbeatAgeSeconds = status.HeartbeatAgeSeconds,
        TransportUnhealthySince = status.TransportUnhealthySince,
        TransportUnhealthySeconds = status.TransportUnhealthySeconds,
        DecommissionEligibleAtUnix = status.DecommissionEligibleAtUnix,
        LiquidationEligibleAtUnix = status.LiquidationEligibleAtUnix,
        RewardEligible = status.RewardEligible,
        ExitSignatureEligible = status.ExitSignatureEligible,
        LiquidationSignatureEligible = status.LiquidationSignatureEligible
    };
}

static ExitLiquidationListItemDto ToExitLiquidationListItem(ServiceNodeObligationStatus status, long currentHeight)
{
    return new ExitLiquidationListItemDto
    {
        Info = new ExitLiquidationNodeInfoDto { BlsPublicKey = status.BlsPublicKey },
        Height = currentHeight,
        LiquidationHeight = status.LiquidationSignatureEligible ? currentHeight : 0,
        ServiceNodePubkey = status.Ed25519PublicKey,
        Type = status.LiquidationSignatureEligible ? "liquidation" : "exit",
        Version = "v2"
    };
}

public partial class Program
{
}
