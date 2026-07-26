using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace XPoint.Staking.Backend.Tests;

public sealed class StakingBackendTests
{
    [Fact]
    public void DeploymentManifestBinding_LoadsAuthoritativeValuesAndRejectsExpectedPinMismatches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"staking-manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, DeploymentManifestJson());
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Contracts:DeploymentManifestPath"] = path,
                ["Contracts:ExpectedDeploymentNetwork"] = "localhost"
            }).Build();
            var binding = DeploymentManifestBinding.Load(configuration);
            Assert.NotNull(binding);
            Assert.Equal(31337, binding!.ChainId);
            Assert.Equal("0x1111111111111111111111111111111111111111", binding.TokenAddress);
            Assert.Equal("100", binding.ToConfigurationOverrides()["Contracts:StakingRequirementAtomic"]);

            var conflict = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Contracts:DeploymentManifestPath"] = path,
                ["Contracts:ExpectedDeploymentNetwork"] = "localhost",
                ["Contracts:ExpectedDeploymentChainId"] = "1"
            }).Build();
            Assert.Throws<InvalidOperationException>(() => DeploymentManifestBinding.Load(conflict));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeploymentManifest_OverridesStockAppSettingsForLocalhost()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"staking-manifest-stock-{Guid.NewGuid():N}.json");
        File.WriteAllText(manifestPath, DeploymentManifestJson());
        var oldPath = Environment.GetEnvironmentVariable("Contracts__DeploymentManifestPath");
        var oldNetwork = Environment.GetEnvironmentVariable("Contracts__ExpectedDeploymentNetwork");
        var oldChainId = Environment.GetEnvironmentVariable("Contracts__ExpectedDeploymentChainId");
        try
        {
            Environment.SetEnvironmentVariable("Contracts__DeploymentManifestPath", manifestPath);
            Environment.SetEnvironmentVariable("Contracts__ExpectedDeploymentNetwork", "localhost");
            Environment.SetEnvironmentVariable("Contracts__ExpectedDeploymentChainId", "31337");
            await using var factory = CreateFactory();
            using var client = factory.CreateClient();
            var info = await client.GetFromJsonAsync<JsonElement>("/info");

            Assert.Equal(31337, info.GetProperty("contracts").GetProperty("chainId").GetInt64());
            Assert.Equal("localhost", info.GetProperty("contracts").GetProperty("networkName").GetString());
            Assert.Equal(100, info.GetProperty("contracts").GetProperty("stakingRequirementAtomic").GetInt64());
        }
        finally
        {
            Environment.SetEnvironmentVariable("Contracts__DeploymentManifestPath", oldPath);
            Environment.SetEnvironmentVariable("Contracts__ExpectedDeploymentNetwork", oldNetwork);
            Environment.SetEnvironmentVariable("Contracts__ExpectedDeploymentChainId", oldChainId);
            File.Delete(manifestPath);
        }
    }

    [Fact]
    public void DeploymentManifestBinding_FailsClosedForMissingOrCorruptManifest()
    {
        var missing = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Contracts:DeploymentManifestPath"] = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")
        }).Build();
        Assert.Throws<InvalidOperationException>(() => DeploymentManifestBinding.Load(missing));

        var path = Path.Combine(Path.GetTempPath(), $"staking-manifest-corrupt-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{");
        try
        {
            var corrupt = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Contracts:DeploymentManifestPath"] = path
            }).Build();
            Assert.Throws<InvalidOperationException>(() => DeploymentManifestBinding.Load(corrupt));
        }
        finally { File.Delete(path); }

        var noLifecyclePath = Path.Combine(Path.GetTempPath(), $"staking-manifest-no-lifecycle-{Guid.NewGuid():N}.json");
        File.WriteAllText(noLifecyclePath, DeploymentManifestJson().Replace("\"lifecycleId\"", "\"removedLifecycleId\"", StringComparison.Ordinal));
        try
        {
            var noLifecycle = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Contracts:DeploymentManifestPath"] = noLifecyclePath,
                ["Contracts:ExpectedDeploymentNetwork"] = "localhost"
            }).Build();
            Assert.Throws<InvalidOperationException>(() => DeploymentManifestBinding.Load(noLifecycle));
        }
        finally { File.Delete(noLifecyclePath); }
    }

    [Fact]
    public async Task ReadinessProbe_RejectsWrongChainAndMissingContractCode()
    {
        var options = Options.Create(ReadinessOptions());
        using var wrongChainHttp = new HttpClient(new ReadinessRpcHandler("0x1", "0x1234"));
        var wrongChain = new StakingReadinessProbe(options, new EthereumJsonRpcClient(wrongChainHttp));
        Assert.False(await wrongChain.IsReadyAsync(CancellationToken.None));

        using var noCodeHttp = new HttpClient(new ReadinessRpcHandler("0x7a69", "0x"));
        var noCode = new StakingReadinessProbe(options, new EthereumJsonRpcClient(noCodeHttp));
        Assert.False(await noCode.IsReadyAsync(CancellationToken.None));
    }

    [Fact]
    public void EventIndexer_QuarantinesStaleLocalStateAndFailsClosedForProduction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"staking-stale-{Guid.NewGuid():N}.json");
        try
        {
            var local = ReadinessOptions() with { StatePath = path, NetworkName = "LocalDev" };
            var writer = new EventIndexer(Options.Create(local));
            writer.Ingest(new ChainEvent { ChainId = 31337, BlockNumber = 1, TransactionHash = "0xstale", LogIndex = 0, Name = "ignored" });
            var changedLocal = local with { TokenAddress = "0x9999999999999999999999999999999999999999" };
            var reloaded = new EventIndexer(Options.Create(changedLocal));
            Assert.Equal(1, reloaded.GetIngestionStats().StaleStateQuarantines);
            Assert.Empty(reloaded.GetEvents());

            var production = ReadinessOptions() with { StatePath = path, NetworkName = "mainnet" };
            var productionWriter = new EventIndexer(Options.Create(production));
            productionWriter.Ingest(new ChainEvent { ChainId = 31337, BlockNumber = 1, TransactionHash = "0xproduction", LogIndex = 0, Name = "ignored" });
            Assert.Throws<InvalidOperationException>(() => new EventIndexer(Options.Create(production with { TokenAddress = "0x9999999999999999999999999999999999999999" })));
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            var fileName = Path.GetFileName(path);
            foreach (var file in Directory.GetFiles(directory, $"{fileName}*")) { File.Delete(file); }
        }
    }

    [Fact]
    public async Task SessionApi_DoesNotReturnSyntheticPricesWhenProviderIsMissing()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Price:EthereumRpcUrl"] = ""
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/prices/xpnt/7d");

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PriceFeedService_UsesConfiguredUniswapPoolForHistory()
    {
        const string pool = "0x5d42A2b90867B813B753f5B49877Ea7c6bB2B603";
        const string xpnt = "0x63b2cdb8b0d8774f1fdca91d24803698582a079f";
        const string usdc = "0xaf88d065e77c8cc2239327c5edb3a432268e5831";
        using var http = new HttpClient(new RpcStubHandler((to, data) =>
        {
            return data switch
            {
                "0x0dfe1681" => AbiAddress(xpnt),
                "0xd21220a7" => AbiAddress(usdc),
                "0x313ce567" when string.Equals(to, xpnt, StringComparison.OrdinalIgnoreCase) => AbiUInt256(6),
                "0x313ce567" when string.Equals(to, usdc, StringComparison.OrdinalIgnoreCase) => AbiUInt256(6),
                "0x3850c7bd" => AbiUInt256(BigInteger.One << 96),
                _ => throw new InvalidOperationException($"Unexpected eth_call to {to} with {data}.")
            };
        }));
        var service = new PriceFeedService(
            http,
            Options.Create(new BackendPriceOptions
            {
                EthereumRpcUrl = "https://rpc.example/",
                UniswapPoolAddress = pool,
                BaseTokenAddress = xpnt,
                QuoteTokenAddress = usdc
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PriceFeedService>.Instance);

        var history = await service.GetPriceHistoryAsync("xpnt", 7, CancellationToken.None);

        Assert.Equal(8, history.Count);
        Assert.All(history, point => Assert.Equal(1.0, point.Price));
    }

    [Fact]
    public async Task ChainTipApi_UpdatesNetworkHeightWithoutContractEvents()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chain-tip", new
        {
            blockNumber = 12345,
            blockHash = "0xabc"
        });

        response.EnsureSuccessStatusCode();
        var info = await client.GetFromJsonAsync<JsonElement>("/info");
        Assert.Equal(12345, info.GetProperty("network").GetProperty("block_height").GetInt64());
        Assert.Equal("0xabc", info.GetProperty("network").GetProperty("block_hash").GetString());
    }

    [Fact]
    public async Task RegistryRegistrationClient_ProjectsPreparedRegistrationsForWallet()
    {
        const string operatorAddress = "0xb0ce3b1229c00d1b85c7083e31dae531f3b352c0";
        var blsPublicKey = new string('a', 256);
        var blsSignature = new string('b', 512);
        var ed25519PublicKey = new string('0', 63) + "1";
        var ed25519Signature1 = new string('c', 64);
        var ed25519Signature2 = new string('d', 64);
        var registryJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                nodeId = ed25519PublicKey,
                operatorAddress,
                rewardsAddress = operatorAddress,
                blsPublicKey = new { data = blsPublicKey },
                blsSignature,
                ed25519PublicKey,
                ed25519Signature1,
                ed25519Signature2,
                contributors = new[]
                {
                    new { address = operatorAddress, beneficiary = operatorAddress }
                },
                updatedAt = DateTimeOffset.FromUnixTimeSeconds(1_789_000_000)
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var http = new HttpClient(new StubHandler(registryJson))
        {
            BaseAddress = new Uri("http://registry/")
        };
        var client = new RegistryRegistrationClient(
            http,
            Options.Create(new BackendRegistryOptions { BaseUrl = "http://registry/" }));

        var registrations = await client.GetRegistrationsAsync(operatorAddress, CancellationToken.None);

        var registration = Assert.Single(registrations);
        Assert.Equal(operatorAddress, registration.Operator);
        Assert.Equal(blsPublicKey, registration.BlsPublicKey);
        Assert.Equal(blsSignature, registration.BlsSignature);
        Assert.Equal(ed25519PublicKey, registration.Ed25519PublicKey);
        Assert.Equal(ed25519Signature1 + ed25519Signature2, registration.Ed25519Signature);
    }

    [Fact]
    public async Task RegistryRegistrationClient_ProjectsSigningNodes()
    {
        var blsPublicKey = new string('a', 256);
        var ed25519PublicKey = new string('0', 63) + "1";
        var registryJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                nodeId = ed25519PublicKey,
                operatorAddress = "0xb0ce3b1229c00d1b85c7083e31dae531f3b352c0",
                rewardsAddress = "0xb0ce3b1229c00d1b85c7083e31dae531f3b352c0",
                blsPublicKey = new { data = "0x" + blsPublicKey },
                ed25519PublicKey,
                signingEndpoint = "http://xnode-1:8080/api/staking/quorum/sign/",
                transportStatus = new
                {
                    enabled = true,
                    running = true,
                    degraded = false,
                    mode = "running",
                    mocked = false,
                    restartCount = 0,
                    consecutiveFailures = 0
                },
                transportHealthySince = DateTimeOffset.FromUnixTimeSeconds(1_789_000_000),
                updatedAt = DateTimeOffset.FromUnixTimeSeconds(1_789_000_000)
            },
            new
            {
                nodeId = new string('0', 63) + "2",
                operatorAddress = "0xb0ce3b1229c00d1b85c7083e31dae531f3b352c0",
                rewardsAddress = "0xb0ce3b1229c00d1b85c7083e31dae531f3b352c0",
                blsPublicKey = new { data = new string('b', 256) },
                ed25519PublicKey = new string('0', 63) + "2",
                signingEndpoint = "not-a-url",
                transportStatus = new
                {
                    enabled = true,
                    running = true,
                    degraded = false,
                    mode = "running",
                    mocked = false,
                    restartCount = 0,
                    consecutiveFailures = 0
                },
                transportHealthySince = DateTimeOffset.FromUnixTimeSeconds(1_789_000_001),
                updatedAt = DateTimeOffset.FromUnixTimeSeconds(1_789_000_001)
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var http = new HttpClient(new StubHandler(registryJson))
        {
            BaseAddress = new Uri("http://registry/")
        };
        var client = new RegistryRegistrationClient(
            http,
            Options.Create(new BackendRegistryOptions { BaseUrl = "http://registry/" }));

        var nodes = await client.GetSigningNodesAsync(CancellationToken.None);

        var node = Assert.Single(nodes);
        Assert.Equal(ed25519PublicKey, node.NodeId);
        Assert.Equal(blsPublicKey, node.BlsPublicKey);
        Assert.Equal("http://xnode-1:8080/api/staking/quorum/sign", node.SigningEndpoint);
        Assert.True(node.TransportStatus?.Running);
        Assert.False(node.TransportStatus?.Mocked);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_789_000_000), node.TransportHealthySince);
    }

    [Fact]
    public async Task ServiceNodeObligations_MarkHealthyNodeRewardEligible()
    {
        var blsPublicKey = new string('a', 256);
        var now = DateTimeOffset.UtcNow;
        using var harness = CreateObligationHarness(
            blsPublicKey,
            now,
            new
            {
                enabled = true,
                running = true,
                degraded = false,
                mode = "running",
                mocked = false,
                restartCount = 0,
                consecutiveFailures = 0
            },
            transportHealthySince: now.AddMinutes(-5),
            transportUnhealthySince: null);

        var statuses = await harness.Service.GetStatusesAsync(CancellationToken.None);

        var status = Assert.Single(statuses);
        Assert.Equal("active", status.Status);
        Assert.True(status.RewardEligible);
        Assert.False(status.ExitSignatureEligible);
        Assert.False(status.LiquidationSignatureEligible);
    }

    [Fact]
    public async Task ServiceNodeObligations_MarkLongUnhealthyTransportLiquidatable()
    {
        var blsPublicKey = new string('b', 256);
        var now = DateTimeOffset.UtcNow;
        using var harness = CreateObligationHarness(
            blsPublicKey,
            now,
            new
            {
                enabled = true,
                running = false,
                degraded = true,
                mode = "degraded",
                mocked = false,
                restartCount = 5,
                consecutiveFailures = 5
            },
            transportHealthySince: null,
            transportUnhealthySince: now.AddSeconds(-700));

        var status = Assert.Single(await harness.Service.GetStatusesAsync(CancellationToken.None));

        Assert.Equal("liquidatable", status.Status);
        Assert.False(status.RewardEligible);
        Assert.True(status.LiquidationSignatureEligible);
    }

    [Fact]
    public async Task ServiceNodeObligations_MarkExitRequestedNodeExitEligible()
    {
        var blsPublicKey = new string('c', 256);
        var now = DateTimeOffset.UtcNow;
        using var harness = CreateObligationHarness(
            blsPublicKey,
            now,
            new
            {
                enabled = true,
                running = true,
                degraded = false,
                mode = "running",
                mocked = false,
                restartCount = 0,
                consecutiveFailures = 0
            },
            transportHealthySince: now.AddMinutes(-5),
            transportUnhealthySince: null);
        harness.Indexer.Ingest(new ChainEvent
        {
            ChainId = 31337,
            BlockNumber = 2,
            TransactionHash = "0xexit-request",
            LogIndex = 0,
            Address = "0xrewards",
            Name = "ServiceNodeExitRequest",
            Args = Args(new { serviceNodeID = "1" })
        });

        var status = Assert.Single(await harness.Service.GetStatusesAsync(CancellationToken.None));

        Assert.Equal("exit-requested", status.Status);
        Assert.True(status.ExitSignatureEligible);
        Assert.False(status.LiquidationSignatureEligible);
        Assert.False(status.RewardEligible);
    }

    [Fact]
    public async Task NetworkQuorumSigning_UsesOnChainActiveSignerSetEvenWhenNodeIsNotRewardEligible()
    {
        var blsPublicKey = new string('d', 256);
        var now = DateTimeOffset.UtcNow;
        using var harness = CreateObligationHarness(
            blsPublicKey,
            now,
            new
            {
                enabled = true,
                running = false,
                degraded = true,
                mode = "degraded",
                mocked = false,
                restartCount = 3,
                consecutiveFailures = 3
            },
            transportHealthySince: null,
            transportUnhealthySince: now.AddMinutes(-1));
        var signerHandler = new ThrowingRequestHandler();
        using var signerHttp = new HttpClient(signerHandler);
        using var rpcHttp = new HttpClient(new ThrowingRequestHandler());
        var options = new BackendContractOptions();
        var rewards = new RewardStateService(
            harness.Indexer,
            new EthereumJsonRpcClient(rpcHttp),
            Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RewardStateService>.Instance);
        var signer = new NetworkBlsRewardSignatureService(
            harness.Indexer,
            rewards,
            harness.Registry,
            harness.Service,
            signerHttp,
            new TestOptionsMonitor<BackendContractOptions>(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NetworkBlsRewardSignatureService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            signer.CreateRewardsSignatureAsync(
                "0x1111111111111111111111111111111111111111",
                CancellationToken.None));

        Assert.Contains("No service nodes returned", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, signerHandler.RequestCount);
    }

    [Fact]
    public async Task NetworkQuorumSigning_RefusesWhenCollectedSignaturesDoNotMeetThreshold()
    {
        var blsKeys = new[]
        {
            new string('a', 256),
            new string('b', 256),
            new string('c', 256)
        };
        using var harness = CreateQuorumHarness(blsKeys);
        var signerHandler = new SelectiveSignerHandler(blsKeys[0]);
        using var signerHttp = new HttpClient(signerHandler);
        using var rpcHttp = new HttpClient(new ThrowingRequestHandler());
        var options = new BackendContractOptions
        {
            QuorumNonSignerThresholdMax = 4000
        };
        var rewards = new RewardStateService(
            harness.Indexer,
            new EthereumJsonRpcClient(rpcHttp),
            Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RewardStateService>.Instance);
        var signer = new NetworkBlsRewardSignatureService(
            harness.Indexer,
            rewards,
            harness.Registry,
            harness.Service,
            signerHttp,
            new TestOptionsMonitor<BackendContractOptions>(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NetworkBlsRewardSignatureService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            signer.CreateRewardsSignatureAsync(
                "0x1111111111111111111111111111111111111111",
                CancellationToken.None));

        Assert.Contains("Insufficient quorum signatures", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, signerHandler.RequestCount);
        Assert.Equal(3, signerHandler.PolicyQuotes.Count);
        Assert.Single(signerHandler.PolicyQuotes.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.All(signerHandler.PolicyQuotes, quote => Assert.False(string.IsNullOrWhiteSpace(quote)));
    }

    [Fact]
    public async Task SessionApi_ProjectsContributionContractsFromFactoryAndContributionEvents()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        const string contract = "0x3333333333333333333333333333333333333333";
        const string operatorAddress = "0x1111111111111111111111111111111111111111";
        const string contributor = "0x2222222222222222222222222222222222222222";

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 421614,
            blockNumber = 100,
            blockHash = "0xblock100",
            transactionHash = "0xfactory",
            logIndex = 0,
            address = "0xfac0000000000000000000000000000000000000",
            mainArg = contract,
            name = "NewServiceNodeContributionContract",
            args = new
            {
                contributorContract = contract,
                serviceNodePubkey = 12345,
                @operator = operatorAddress
            }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 421614,
            blockNumber = 101,
            transactionHash = "0xpubkeys",
            logIndex = 0,
            address = contract,
            mainArg = contract,
            name = "UpdatePubkeys",
            args = new
            {
                newBLSPubkey = new { X = 1, Y = 2 },
                newEd25519Pubkey = 12345
            }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 421614,
            blockNumber = 102,
            transactionHash = "0xfee",
            logIndex = 0,
            address = contract,
            mainArg = contract,
            name = "UpdateFee",
            args = new { newFee = 1500 }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 421614,
            blockNumber = 103,
            transactionHash = "0xmanual",
            logIndex = 0,
            address = contract,
            mainArg = contract,
            name = "UpdateManualFinalize",
            args = new { newValue = true }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 421614,
            blockNumber = 104,
            transactionHash = "0xcontrib",
            logIndex = 0,
            address = contract,
            mainArg = contract,
            name = "NewContribution",
            args = new
            {
                contributor,
                beneficiary = contributor,
                amount = 120_000_000_000L
            }
        });

        var response = await client.GetFromJsonAsync<JsonElement>("/contract/contribution");
        var projected = response.GetProperty("contracts")[0];

        Assert.Equal(contract, projected.GetProperty("address").GetString());
        Assert.Equal(operatorAddress, projected.GetProperty("operator_address").GetString());
        Assert.Equal("0000000000000000000000000000000000000000000000000000000000003039", projected.GetProperty("service_node_pubkey").GetString());
        Assert.Equal(
            "0000000000000000000000000000000000000000000000000000000000000001" +
            "0000000000000000000000000000000000000000000000000000000000000002",
            projected.GetProperty("pubkey_bls").GetString());
        Assert.Equal(1500, projected.GetProperty("fee").GetInt32());
        Assert.True(projected.GetProperty("manual_finalize").GetBoolean());
        Assert.Equal(1, projected.GetProperty("status").GetInt32());
        Assert.Equal(120_000_000_000L, projected.GetProperty("contributors")[0].GetProperty("amount").GetInt64());
        Assert.Equal(104, response.GetProperty("network").GetProperty("block_height").GetInt64());
    }

    [Fact]
    public async Task SessionApi_ProjectsStakesForWalletInOriginalShape()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string operatorAddress = "0x1111111111111111111111111111111111111111";

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 421614,
            blockNumber = 120,
            transactionHash = "0xstake",
            logIndex = 0,
            address = "0xrewards",
            name = "NewServiceNodeV2",
            args = new
            {
                serviceNodeID = "77",
                pubkey = new { X = 7, Y = 8 },
                serviceNode = new
                {
                    serviceNodePubkey = 999,
                    serviceNodeSignature1 = 1,
                    serviceNodeSignature2 = 2,
                    fee = 1200
                },
                contributors = new[]
                {
                    new
                    {
                        staker = new { addr = operatorAddress, beneficiary = operatorAddress },
                        stakedAmount = 120_000_000_000L
                    }
                }
            }
        });

        var response = await client.GetFromJsonAsync<JsonElement>($"/stakes/{operatorAddress}");
        var stake = response.GetProperty("stakes")[0];

        Assert.True(stake.GetProperty("active").GetBoolean());
        Assert.Equal(77, stake.GetProperty("contract_id").GetInt32());
        Assert.Equal(operatorAddress, stake.GetProperty("operator_address").GetString());
        Assert.Equal("00000000000000000000000000000000000000000000000000000000000003e7", stake.GetProperty("service_node_pubkey").GetString());
        Assert.Equal(120_000_000_000L, stake.GetProperty("total_contributed").GetInt64());
        Assert.Equal(1200, stake.GetProperty("operator_fee").GetInt32());
    }

    [Fact]
    public async Task SessionApi_UsesFirstExitRequestForUnlockHeight()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Contracts:ExitRequestTimeSeconds"] = "100",
            ["Contracts:ChainBlockTimeMilliseconds"] = "250"
        });
        using var client = factory.CreateClient();
        const string operatorAddress = "0x1111111111111111111111111111111111111111";

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 421614,
            blockNumber = 1000,
            transactionHash = "0xstake-exit",
            logIndex = 0,
            address = "0xrewards",
            name = "NewServiceNodeV2",
            args = new
            {
                serviceNodeID = "88",
                pubkey = new { X = 7, Y = 8 },
                serviceNode = new
                {
                    serviceNodePubkey = 88,
                    serviceNodeSignature1 = 1,
                    serviceNodeSignature2 = 2,
                    fee = 0
                },
                contributors = new[]
                {
                    new
                    {
                        staker = new { addr = operatorAddress, beneficiary = operatorAddress },
                        stakedAmount = 120_000_000_000L
                    }
                }
            }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 421614,
            blockNumber = 1100,
            transactionHash = "0xexit-first",
            logIndex = 0,
            address = "0xrewards",
            name = "ServiceNodeExitRequest",
            args = new { serviceNodeID = "88" }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 421614,
            blockNumber = 1200,
            transactionHash = "0xexit-duplicate",
            logIndex = 0,
            address = "0xrewards",
            name = "ServiceNodeExitRequest",
            args = new { serviceNodeID = "88" }
        });

        var response = await client.GetFromJsonAsync<JsonElement>($"/stakes/{operatorAddress}");
        var stake = response.GetProperty("stakes")[0];

        Assert.True(stake.GetProperty("active").GetBoolean());
        Assert.Equal(1000, stake.GetProperty("registration_height").GetInt64());
        Assert.Equal(1500, stake.GetProperty("requested_unlock_height").GetInt64());
        Assert.True(stake.GetProperty("last_uptime_proof").GetInt64() > 1_600_000_000L);
    }

    [Fact]
    public async Task SessionApi_FormatsUint256ServiceNodePubkeyAsUnsignedHex()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string operatorAddress = "0x1111111111111111111111111111111111111111";
        const string highBitPubkeyHex = "c08f5aecc314da789193719a15f1fa2e21a1aeadf266a6b53bd667974870c846";
        const string highBitPubkeyDecimal =
            "87097353598513555592571137072258353201044282458783684532809291416874211395654";

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 421614,
            blockNumber = 120,
            transactionHash = "0xstake-high-bit",
            logIndex = 0,
            address = "0xrewards",
            name = "NewServiceNodeV2",
            args = new
            {
                serviceNodeID = "77",
                pubkey = new { X = 7, Y = 8 },
                serviceNode = new
                {
                    serviceNodePubkey = highBitPubkeyDecimal,
                    serviceNodeSignature1 = 1,
                    serviceNodeSignature2 = 2,
                    fee = 1200
                },
                contributors = new[]
                {
                    new
                    {
                        staker = new { addr = operatorAddress, beneficiary = operatorAddress },
                        stakedAmount = 120_000_000_000L
                    }
                }
            }
        });

        var response = await client.GetFromJsonAsync<JsonElement>($"/stakes/{operatorAddress}");
        var stake = response.GetProperty("stakes")[0];

        Assert.Equal(highBitPubkeyHex, stake.GetProperty("service_node_pubkey").GetString());
        Assert.Equal(64, stake.GetProperty("service_node_pubkey").GetString()!.Length);
    }

    [Fact]
    public async Task EventIndexer_IsIdempotentForReplay()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var chainEvent = new
        {
            chainId = 31337,
            blockNumber = 10,
            transactionHash = "0xabc",
            logIndex = 0,
            address = "0xrewards",
            name = "RewardsClaimed",
            args = new { recipientAddress = "0x1111111111111111111111111111111111111111", amount = 10_000L }
        };

        var first = await client.PostAsJsonAsync("/api/events", chainEvent);
        var second = await client.PostAsJsonAsync("/api/events", chainEvent);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(firstBody.GetProperty("inserted").GetBoolean());
        Assert.False(secondBody.GetProperty("inserted").GetBoolean());
        Assert.Equal(1, secondBody.GetProperty("totalEvents").GetInt64());
    }

    [Fact]
    public async Task NewServiceNodeEvent_ProjectsStakeStateWithOperatorFeeAndContributors()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var chainEvent = new
        {
            chainId = 31337,
            blockNumber = 11,
            transactionHash = "0xdef",
            logIndex = 1,
            address = "0xrewards",
            name = "NewServiceNodeV2",
            args = new
            {
                serviceNodeID = "42",
                initiator = "0x9999999999999999999999999999999999999999",
                pubkey = new { X = "0x01", Y = "0x02" },
                serviceNode = new
                {
                    serviceNodePubkey = "12345",
                    serviceNodeSignature1 = "67890",
                    serviceNodeSignature2 = "13579",
                    fee = 1500
                },
                contributors = new[]
                {
                    new
                    {
                        staker = new
                        {
                            addr = "0x1111111111111111111111111111111111111111",
                            beneficiary = "0x2222222222222222222222222222222222222222"
                        },
                        stakedAmount = 20_000L * 1_000_000_000L
                    }
                }
            }
        };

        var response = await client.PostAsJsonAsync("/api/events", chainEvent);
        response.EnsureSuccessStatusCode();

        var node = await client.GetFromJsonAsync<JsonElement>("/api/staking/nodes/42");
        Assert.Equal("active", node.GetProperty("status").GetString());
        Assert.Equal("0x1111111111111111111111111111111111111111", node.GetProperty("operatorAddress").GetString());
        Assert.Equal("0x01", node.GetProperty("blsPublicKeyX").GetString());
        Assert.Equal("12345", node.GetProperty("serviceNodePubkey").GetString());
        Assert.Equal(1500, node.GetProperty("operatorFeeBps").GetInt32());
        Assert.Equal(20_000L * 1_000_000_000L, node.GetProperty("stakeAtomic").GetInt64());
    }

    [Fact]
    public async Task RewardEvents_ProjectClaimableXpnt()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string wallet = "0x1111111111111111111111111111111111111111";

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 31337,
            blockNumber = 12,
            transactionHash = "0xaaa",
            logIndex = 0,
            address = "0xrewards",
            name = "RewardsBalanceUpdated",
            args = new { recipientAddress = wallet, amount = 50_000L, previousBalance = 0L }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 31337,
            blockNumber = 13,
            transactionHash = "0xaab",
            logIndex = 0,
            address = "0xrewards",
            name = "RewardsClaimed",
            args = new { recipientAddress = wallet, amount = 15_000L }
        });

        var rewards = await client.GetFromJsonAsync<JsonElement>($"/api/staking/rewards/{wallet}");
        Assert.Equal("XPNT", rewards.GetProperty("tokenSymbol").GetString());
        Assert.Equal(35_000L, rewards.GetProperty("claimableRewardsAtomic").GetInt64());
    }

    [Fact]
    public async Task RewardSigningQuote_FreezesAmountForRecipient()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"xpoint-staking-quote-{Guid.NewGuid():N}.json");
        const string wallet = "0x1111111111111111111111111111111111111111";

        try
        {
            var indexer = new EventIndexer(Options.Create(new BackendContractOptions
            {
                StatePath = statePath
            }));
            indexer.ApplyRewardStateFromChain(wallet, 123_000L, 0);

            using var rpcHttp = new HttpClient(new ThrowingRequestHandler());
            var service = new RewardStateService(
                indexer,
                new EthereumJsonRpcClient(rpcHttp),
                Options.Create(new BackendContractOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RewardStateService>.Instance);

            var quote = await service.CreateRewardSigningQuoteAsync(wallet, CancellationToken.None);
            indexer.ApplyRewardStateFromChain(wallet, 124_000L, 0);

            var sameRecipient = service.GetRewardSigningQuote(wallet.ToUpperInvariant(), quote.QuoteId);
            var otherRecipient = service.GetRewardSigningQuote(
                "0x2222222222222222222222222222222222222222",
                quote.QuoteId);

            Assert.NotNull(sameRecipient);
            Assert.Equal(123_000L, sameRecipient!.AmountAtomic);
            Assert.Null(otherRecipient);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public void RewardAccrual_DistributesNetworkRewardsToActiveNodeContributors()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"xpoint-staking-accrual-{Guid.NewGuid():N}.json");
        var indexer = new EventIndexer(Microsoft.Extensions.Options.Options.Create(new BackendContractOptions
        {
            StatePath = statePath,
            RewardPulseSeconds = 120
        }));
        const string wallet = "0x1111111111111111111111111111111111111111";

        try
        {
            indexer.Ingest(new ChainEvent
            {
                ChainId = 31337,
                BlockNumber = 12,
                TransactionHash = "0xaccrual",
                LogIndex = 0,
                Address = "0xrewards",
                Name = "NewServiceNodeV2",
                Args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["serviceNodeID"] = JsonSerializer.Deserialize<JsonElement>("\"46\"")!,
                    ["contributors"] = JsonSerializer.Deserialize<JsonElement>("[{\"staker\":{\"addr\":\"0x1111111111111111111111111111111111111111\",\"beneficiary\":\"0x1111111111111111111111111111111111111111\"},\"stakedAmount\":120000000000}]")!
                }
            });

            var accrued = indexer.ApplyRewardAccrual(12_000L, DateTimeOffset.UtcNow.AddMinutes(2));
            var rewards = indexer.GetRewards(wallet);
            var daily = indexer.GetDailyRewards(wallet);

            Assert.True(accrued > 0);
            Assert.Equal(accrued, rewards.LifetimeRewardsAtomic);
            Assert.Equal(accrued, rewards.ClaimableRewardsAtomic);
            Assert.Single(daily);
            Assert.Equal(accrued, daily[0].LifetimeRewards);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task RewardStateService_ReconcilesLocalRewardsWithOnChainRecipientState()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"xpoint-staking-rewards-chain-{Guid.NewGuid():N}.json");
        try
        {
            var indexer = new EventIndexer(Options.Create(new BackendContractOptions
            {
                StatePath = statePath
            }));
            const string wallet = "0x1111111111111111111111111111111111111111";
            indexer.Ingest(new ChainEvent
            {
                ChainId = 421614,
                BlockNumber = 10,
                TransactionHash = "0xreward-update",
                LogIndex = 0,
                Address = "0x2222222222222222222222222222222222222222",
                Name = "RewardsBalanceUpdated",
                Args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["recipientAddress"] = JsonSerializer.Deserialize<JsonElement>($"\"{wallet}\"")!,
                    ["amount"] = JsonSerializer.Deserialize<JsonElement>("50000")!
                }
            });

            using var http = new HttpClient(new StubHandler(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = "0x" + AbiUInt256(70_000L) + AbiUInt256(30_000L)
            })));
            var service = new RewardStateService(
                indexer,
                new EthereumJsonRpcClient(http),
                Options.Create(new BackendContractOptions
                {
                    EthereumRpcUrl = "http://rpc/",
                    ServiceNodeRewardsAddress = "0x2222222222222222222222222222222222222222"
                }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RewardStateService>.Instance);

            var rewards = await service.GetRewardsAsync(wallet, CancellationToken.None);

            Assert.Equal(70_000L, rewards.LifetimeRewardsAtomic);
            Assert.Equal(30_000L, rewards.ClaimedRewardsAtomic);
            Assert.Equal(40_000L, rewards.ClaimableRewardsAtomic);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task ServiceNodeExit_ProjectsExitedStatusAndReturnedStakeFromAbiShape()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string operatorAddress = "0x1111111111111111111111111111111111111111";

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 31337,
            blockNumber = 20,
            transactionHash = "0xexit-source",
            logIndex = 0,
            address = "0xrewards",
            name = "NewServiceNodeV2",
            args = new
            {
                serviceNodeID = "43",
                initiator = "0x9999999999999999999999999999999999999999",
                pubkey = new { X = "0x03", Y = "0x04" },
                serviceNode = new { serviceNodePubkey = "24680", serviceNodeSignature1 = "1", serviceNodeSignature2 = "2", fee = 0 },
                contributors = new[]
                {
                    new
                    {
                        staker = new { addr = operatorAddress, beneficiary = operatorAddress },
                        stakedAmount = 20_000L * 1_000_000_000L
                    }
                }
            }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 31337,
            blockNumber = 21,
            transactionHash = "0xexit",
            logIndex = 0,
            address = "0xrewards",
            name = "ServiceNodeExit",
            args = new
            {
                serviceNodeID = "43",
                initiator = operatorAddress,
                returnedAmount = 20_000L * 1_000_000_000L,
                pubkey = new { X = "0x03", Y = "0x04" }
            }
        });

        var node = await client.GetFromJsonAsync<JsonElement>("/api/staking/nodes/43");
        Assert.Equal("exited", node.GetProperty("status").GetString());

        var rewards = await client.GetFromJsonAsync<JsonElement>($"/api/staking/rewards/{operatorAddress}");
        Assert.Equal(20_000L * 1_000_000_000L, rewards.GetProperty("claimedStakesAtomic").GetInt64());
    }

    [Fact]
    public void EventIndexer_PersistsAndReloadsReplaySafeState()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"xpoint-staking-{Guid.NewGuid():N}.json");
        try
        {
            var indexer = new EventIndexer(Microsoft.Extensions.Options.Options.Create(new BackendContractOptions
            {
                StatePath = statePath
            }));

            var chainEvent = new ChainEvent
            {
                ChainId = 31337,
                BlockNumber = 10,
                TransactionHash = "0xabc",
                LogIndex = 0,
                Address = "0xrewards",
                Name = "RewardsClaimed",
                Args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["recipientAddress"] = JsonSerializer.Deserialize<JsonElement>("\"0x1111111111111111111111111111111111111111\"")!,
                    ["amount"] = JsonSerializer.Deserialize<JsonElement>("10000")!
                }
            };

            var first = indexer.Ingest(chainEvent);
            Assert.True(first.Inserted);
            Assert.True(File.Exists(statePath));

            var reloaded = new EventIndexer(Microsoft.Extensions.Options.Options.Create(new BackendContractOptions
            {
                StatePath = statePath
            }));

            var rewards = reloaded.GetRewards("0x1111111111111111111111111111111111111111");
            Assert.Equal(10_000L, rewards.ClaimedRewardsAtomic);

            var duplicate = reloaded.Ingest(chainEvent);
            Assert.False(duplicate.Inserted);
            Assert.Equal(1, duplicate.TotalEvents);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public void EventIndexer_IgnoresLateStatusEventsThatWouldRegressNodeState()
    {
        var indexer = new EventIndexer(Microsoft.Extensions.Options.Options.Create(new BackendContractOptions()));

        var created = new ChainEvent
        {
            ChainId = 31337,
            BlockNumber = 30,
            TransactionHash = "0xcreated",
            LogIndex = 0,
            Address = "0xrewards",
            Name = "NewServiceNodeV2",
            Args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["serviceNodeID"] = JsonSerializer.Deserialize<JsonElement>("\"44\"")!,
                ["contributors"] = JsonSerializer.Deserialize<JsonElement>("[{\"staker\":{\"addr\":\"0x1111111111111111111111111111111111111111\",\"beneficiary\":\"0x1111111111111111111111111111111111111111\"},\"stakedAmount\":1}]")!
            }
        };

        var exited = created with
        {
            BlockNumber = 32,
            TransactionHash = "0xexit",
            Name = "ServiceNodeExit",
            Args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["serviceNodeID"] = JsonSerializer.Deserialize<JsonElement>("\"44\"")!
            }
        };

        var staleExitRequest = created with
        {
            BlockNumber = 31,
            TransactionHash = "0xlate",
            Name = "ServiceNodeExitRequest",
            Args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["serviceNodeID"] = JsonSerializer.Deserialize<JsonElement>("\"44\"")!
            }
        };

        indexer.Ingest(created);
        indexer.Ingest(exited);
        indexer.Ingest(staleExitRequest);

        var node = indexer.GetNode("44");
        Assert.NotNull(node);
        Assert.Equal("exited", node!.Status);
        Assert.Equal(32, node.LastBlockNumber);
    }

    [Fact]
    public void EventIndexer_IgnoresLateNodeProjectionEventsThatWouldOverwriteNewerData()
    {
        var indexer = new EventIndexer(Microsoft.Extensions.Options.Options.Create(new BackendContractOptions()));

        var newer = new ChainEvent
        {
            ChainId = 31337,
            BlockNumber = 40,
            TransactionHash = "0xnewer",
            LogIndex = 0,
            Address = "0xrewards",
            Name = "NewServiceNodeV2",
            Args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["serviceNodeID"] = JsonSerializer.Deserialize<JsonElement>("\"45\"")!,
                ["operator"] = JsonSerializer.Deserialize<JsonElement>("\"0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")!,
                ["contributors"] = JsonSerializer.Deserialize<JsonElement>("[{\"staker\":{\"addr\":\"0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"beneficiary\":\"0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},\"stakedAmount\":10}]")!
            }
        };

        var stale = newer with
        {
            BlockNumber = 39,
            TransactionHash = "0xstale",
            Args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["serviceNodeID"] = JsonSerializer.Deserialize<JsonElement>("\"45\"")!,
                ["operator"] = JsonSerializer.Deserialize<JsonElement>("\"0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"")!,
                ["contributors"] = JsonSerializer.Deserialize<JsonElement>("[{\"staker\":{\"addr\":\"0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"beneficiary\":\"0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"},\"stakedAmount\":1}]")!
            }
        };

        indexer.Ingest(newer);
        indexer.Ingest(stale);

        var node = indexer.GetNode("45");
        Assert.NotNull(node);
        Assert.Equal("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", node!.OperatorAddress);
        Assert.Equal(40, node.LastBlockNumber);
    }

    [Fact]
    public void EventIndexer_RecoversFromCorruptedStateFile()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"xpoint-staking-corrupt-{Guid.NewGuid():N}.json");
        File.WriteAllText(statePath, "{ this is not valid json");

        try
        {
            var indexer = new EventIndexer(Microsoft.Extensions.Options.Options.Create(new BackendContractOptions
            {
                StatePath = statePath
            }));

            var stats = indexer.GetIngestionStats();
            Assert.Equal(1, stats.CorruptedStateRecoveries);
            Assert.Equal(0, stats.TotalEvents);

            Assert.False(File.Exists(statePath));

            var directory = Path.GetDirectoryName(statePath)!;
            var fileName = Path.GetFileName(statePath);
            var backups = Directory.GetFiles(directory, $"{fileName}.corrupt-*.bak");
            Assert.Single(backups);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            var directory = Path.GetDirectoryName(statePath)!;
            var fileName = Path.GetFileName(statePath);
            foreach (var backup in Directory.GetFiles(directory, $"{fileName}.corrupt-*.bak"))
            {
                File.Delete(backup);
            }
        }
    }

    [Fact]
    public async Task EventIngestionStats_TrackDuplicateAndLateEvents()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 31337,
            blockNumber = 50,
            transactionHash = "0xdup-1",
            logIndex = 0,
            address = "0xrewards",
            name = "NewServiceNodeV2",
            args = new
            {
                serviceNodeID = "stats-node-1",
                contributors = new[]
                {
                    new
                    {
                        staker = new { addr = "0x1111111111111111111111111111111111111111", beneficiary = "0x1111111111111111111111111111111111111111" },
                        stakedAmount = 10L
                    }
                }
            }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 31337,
            blockNumber = 50,
            transactionHash = "0xdup-1",
            logIndex = 0,
            address = "0xrewards",
            name = "NewServiceNodeV2",
            args = new
            {
                serviceNodeID = "stats-node-1",
                contributors = new[]
                {
                    new
                    {
                        staker = new { addr = "0x1111111111111111111111111111111111111111", beneficiary = "0x1111111111111111111111111111111111111111" },
                        stakedAmount = 10L
                    }
                }
            }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 31337,
            blockNumber = 52,
            transactionHash = "0xstatus-newer",
            logIndex = 0,
            address = "0xrewards",
            name = "ServiceNodeExit",
            args = new { serviceNodeID = "stats-node-1" }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 31337,
            blockNumber = 51,
            transactionHash = "0xstatus-stale",
            logIndex = 0,
            address = "0xrewards",
            name = "ServiceNodeExitRequest",
            args = new { serviceNodeID = "stats-node-1" }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 31337,
            blockNumber = 54,
            transactionHash = "0xnode-newer",
            logIndex = 0,
            address = "0xrewards",
            name = "NewServiceNodeV2",
            args = new
            {
                serviceNodeID = "stats-node-2",
                @operator = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                contributors = new[]
                {
                    new
                    {
                        staker = new { addr = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", beneficiary = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                        stakedAmount = 10L
                    }
                }
            }
        });

        await client.PostAsJsonAsync("/api/events", new
        {
            chainId = 31337,
            blockNumber = 53,
            transactionHash = "0xnode-stale",
            logIndex = 0,
            address = "0xrewards",
            name = "NewServiceNodeV2",
            args = new
            {
                serviceNodeID = "stats-node-2",
                @operator = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                contributors = new[]
                {
                    new
                    {
                        staker = new { addr = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", beneficiary = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
                        stakedAmount = 1L
                    }
                }
            }
        });

        var stats = await client.GetFromJsonAsync<JsonElement>("/api/events/stats");

        Assert.Equal(6, stats.GetProperty("attempted").GetInt64());
        Assert.Equal(5, stats.GetProperty("inserted").GetInt64());
        Assert.Equal(1, stats.GetProperty("duplicates").GetInt64());
        Assert.Equal(1, stats.GetProperty("staleStatusIgnored").GetInt64());
        Assert.Equal(1, stats.GetProperty("staleNodeProjectionIgnored").GetInt64());
        Assert.Equal(0, stats.GetProperty("corruptedStateRecoveries").GetInt64());
        Assert.Equal(0, stats.GetProperty("statePersistenceFailures").GetInt64());
        Assert.Equal(5, stats.GetProperty("totalEvents").GetInt64());
    }

    [Fact]
    public async Task CorruptedStateFile_ExposesRecoveryCountViaApiStats()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"xpoint-staking-api-corrupt-{Guid.NewGuid():N}.json");
        File.WriteAllText(statePath, "{ not valid json");

        try
        {
            await using var factory = CreateFactory(statePath);
            using var client = factory.CreateClient();

            var stats = await client.GetFromJsonAsync<JsonElement>("/api/events/stats");
            Assert.Equal(1, stats.GetProperty("corruptedStateRecoveries").GetInt64());

            var backups = Directory.GetFiles(Path.GetDirectoryName(statePath)!, $"{Path.GetFileName(statePath)}.corrupt-*.bak");
            Assert.Single(backups);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(statePath)!, $"{Path.GetFileName(statePath)}.corrupt-*.bak"))
            {
                File.Delete(backup);
            }
        }
    }

    [Fact]
    public async Task Ingest_WhenStatePersistenceFails_RemainsSuccessfulAndTracksFailureMetric()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), $"xpoint-staking-state-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDirectory);

        try
        {
            await using var factory = CreateFactory(stateDirectory);
            using var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/events", new
            {
                chainId = 31337,
                blockNumber = 60,
                transactionHash = "0xpersist-fail",
                logIndex = 0,
                address = "0xrewards",
                name = "RewardsClaimed",
                args = new { recipientAddress = "0x1111111111111111111111111111111111111111", amount = 1L }
            });

            response.EnsureSuccessStatusCode();

            var stats = await client.GetFromJsonAsync<JsonElement>("/api/events/stats");
            Assert.Equal(1, stats.GetProperty("attempted").GetInt64());
            Assert.Equal(1, stats.GetProperty("inserted").GetInt64());
            Assert.Equal(1, stats.GetProperty("statePersistenceFailures").GetInt64());
            Assert.Equal(1, stats.GetProperty("totalEvents").GetInt64());
        }
        finally
        {
            Directory.Delete(stateDirectory, true);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"xpoint-staking-api-{Guid.NewGuid():N}.json");
        return CreateFactory(statePath, settings);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string statePath,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Contracts:StatePath"] = statePath
                };
                if (settings is not null)
                {
                    foreach (var setting in settings)
                    {
                        values[setting.Key] = setting.Value;
                    }
                }

                config.AddInMemoryCollection(values);
            });
        });
    }

    private static ObligationHarness CreateObligationHarness(
        string blsPublicKey,
        DateTimeOffset updatedAt,
        object transportStatus,
        DateTimeOffset? transportHealthySince,
        DateTimeOffset? transportUnhealthySince)
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"deep-obligations-{Guid.NewGuid():N}.json");
        var indexer = new EventIndexer(Options.Create(new BackendContractOptions
        {
            StatePath = statePath
        }));
        indexer.Ingest(new ChainEvent
        {
            ChainId = 31337,
            BlockNumber = 1,
            TransactionHash = "0xnode-created",
            LogIndex = 0,
            Address = "0xrewards",
            Name = "NewServiceNodeV2",
            Args = Args(new
            {
                serviceNodeID = "1",
                blsData = blsPublicKey,
                serviceNode = new { serviceNodePubkey = "1", fee = 0 },
                contributors = new[]
                {
                    new
                    {
                        staker = new
                        {
                            addr = "0x1111111111111111111111111111111111111111",
                            beneficiary = "0x1111111111111111111111111111111111111111"
                        },
                        stakedAmount = 20_000L * 1_000_000_000L
                    }
                }
            })
        });

        var registryJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                nodeId = new string('0', 63) + "1",
                operatorAddress = "0x1111111111111111111111111111111111111111",
                rewardsAddress = "0x1111111111111111111111111111111111111111",
                blsPublicKey = new { data = blsPublicKey },
                ed25519PublicKey = new string('0', 63) + "1",
                signingEndpoint = "http://xnode-1:8080/api/staking/quorum/sign",
                transportStatus,
                transportHealthySince,
                transportUnhealthySince,
                updatedAt
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var http = new HttpClient(new StubHandler(registryJson))
        {
            BaseAddress = new Uri("http://registry/")
        };
        var registry = new RegistryRegistrationClient(
            http,
            Options.Create(new BackendRegistryOptions { BaseUrl = "http://registry/" }));
        var service = new ServiceNodeObligationService(
            indexer,
            registry,
            Options.Create(new BackendRegistryOptions
            {
                BaseUrl = "http://registry/",
                HeartbeatGraceSeconds = 120,
                DecommissionGraceSeconds = 300,
                LiquidationGraceSeconds = 600
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServiceNodeObligationService>.Instance);
        return new ObligationHarness(statePath, http, indexer, registry, service);
    }

    private static ObligationHarness CreateQuorumHarness(IReadOnlyList<string> blsPublicKeys)
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"deep-quorum-{Guid.NewGuid():N}.json");
        var indexer = new EventIndexer(Options.Create(new BackendContractOptions
        {
            StatePath = statePath
        }));
        for (var i = 0; i < blsPublicKeys.Count; i++)
        {
            var nodeId = i + 1;
            indexer.Ingest(new ChainEvent
            {
                ChainId = 31337,
                BlockNumber = nodeId,
                TransactionHash = $"0xnode-created-{nodeId}",
                LogIndex = 0,
                Address = "0xrewards",
                Name = "NewServiceNodeV2",
                Args = Args(new
                {
                    serviceNodeID = nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    blsData = blsPublicKeys[i],
                    serviceNode = new { serviceNodePubkey = nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture), fee = 0 },
                    contributors = new[]
                    {
                        new
                        {
                            staker = new
                            {
                                addr = "0x1111111111111111111111111111111111111111",
                                beneficiary = "0x1111111111111111111111111111111111111111"
                            },
                            stakedAmount = 20_000L * 1_000_000_000L
                        }
                    }
                })
            });
        }

        var now = DateTimeOffset.UtcNow;
        var registryJson = JsonSerializer.Serialize(
            blsPublicKeys.Select((key, index) => new
            {
                nodeId = new string('0', 63) + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                operatorAddress = "0x1111111111111111111111111111111111111111",
                rewardsAddress = "0x1111111111111111111111111111111111111111",
                blsPublicKey = new { data = key },
                ed25519PublicKey = new string('0', 63) + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                signingEndpoint = $"http://xnode-{index + 1}:8080/api/staking/quorum/sign",
                transportStatus = new
                {
                    enabled = true,
                    running = true,
                    degraded = false,
                    mode = "running",
                    mocked = false,
                    restartCount = 0,
                    consecutiveFailures = 0
                },
                transportHealthySince = now.AddMinutes(-5),
                transportUnhealthySince = (DateTimeOffset?)null,
                updatedAt = now
            }).ToArray(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var http = new HttpClient(new StubHandler(registryJson))
        {
            BaseAddress = new Uri("http://registry/")
        };
        var registry = new RegistryRegistrationClient(
            http,
            Options.Create(new BackendRegistryOptions { BaseUrl = "http://registry/" }));
        var service = new ServiceNodeObligationService(
            indexer,
            registry,
            Options.Create(new BackendRegistryOptions
            {
                BaseUrl = "http://registry/",
                HeartbeatGraceSeconds = 120,
                DecommissionGraceSeconds = 300,
                LiquidationGraceSeconds = 600
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServiceNodeObligationService>.Instance);
        return new ObligationHarness(statePath, http, indexer, registry, service);
    }

    private static Dictionary<string, JsonElement> Args(object value)
    {
        return JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string AbiUInt256(long value)
    {
        return value.ToString("x").PadLeft(64, '0');
    }

    private static string AbiUInt256(BigInteger value)
    {
        return value.ToString("x").PadLeft(64, '0');
    }

    private static string AbiAddress(string address)
    {
        var normalized = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? address[2..]
            : address;
        return normalized.PadLeft(64, '0').ToLowerInvariant();
    }

    private static BackendContractOptions ReadinessOptions() => new()
    {
        ChainId = 31337,
        NetworkName = "localhost",
        EthereumRpcUrl = "http://rpc.test",
        TokenAddress = "0x1111111111111111111111111111111111111111",
        ServiceNodeRewardsAddress = "0x2222222222222222222222222222222222222222",
        RewardRatePoolAddress = "0x3333333333333333333333333333333333333333",
        ServiceNodeContributionFactoryAddress = "0x4444444444444444444444444444444444444444",
        StakingRequirementAtomic = 100,
        MaxStakers = 10
    };

    private static string DeploymentManifestJson() => """
        {
          "schemaVersion": 1,
          "network": "localhost",
          "chainId": 31337,
          "lifecycleId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "contracts": {
            "token": "0x1111111111111111111111111111111111111111",
            "serviceNodeRewards": "0x2222222222222222222222222222222222222222",
            "rewardRatePool": "0x3333333333333333333333333333333333333333",
            "serviceNodeContributionFactory": "0x4444444444444444444444444444444444444444"
          },
          "parameters": { "stakingRequirement": "100", "maxContributors": 10 }
        }
        """;

    private sealed class ObligationHarness : IDisposable
    {
        private readonly string _statePath;
        private readonly HttpClient _http;

        public ObligationHarness(
            string statePath,
            HttpClient http,
            EventIndexer indexer,
            RegistryRegistrationClient registry,
            ServiceNodeObligationService service)
        {
            _statePath = statePath;
            _http = http;
            Indexer = indexer;
            Registry = registry;
            Service = service;
        }

        public EventIndexer Indexer { get; }

        public RegistryRegistrationClient Registry { get; }

        public ServiceNodeObligationService Service { get; }

        public void Dispose()
        {
            _http.Dispose();
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHandler(string json)
        {
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class RpcStubHandler : HttpMessageHandler
    {
        private readonly Func<string, string, string> _respond;

        public RpcStubHandler(Func<string, string, string> respond)
        {
            _respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var call = document.RootElement.GetProperty("params")[0];
            var to = call.GetProperty("to").GetString()!;
            var data = call.GetProperty("data").GetString()!;
            var result = _respond(to, data);
            if (!result.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                result = "0x" + result;
            }

            var json = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                result
            });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ReadinessRpcHandler : HttpMessageHandler
    {
        private readonly string _chainId;
        private readonly string _code;

        public ReadinessRpcHandler(string chainId, string code)
        {
            _chainId = chainId;
            _code = code;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var method = document.RootElement.GetProperty("method").GetString();
            var result = method == "eth_chainId" ? _chainId : _code;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result }), System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingRequestHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("Unexpected HTTP request during test.");
        }
    }

    private sealed class SelectiveSignerHandler : HttpMessageHandler
    {
        private readonly string _signedBlsPublicKey;

        public SelectiveSignerHandler(string signedBlsPublicKey)
        {
            _signedBlsPublicKey = signedBlsPublicKey;
        }

        public int RequestCount { get; private set; }
        public List<string> PolicyQuotes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var amount = document.RootElement.GetProperty("amount").GetInt64();
            PolicyQuotes.Add(document.RootElement.GetProperty("policyQuote").GetString() ?? "");
            var host = request.RequestUri?.Host ?? "";
            if (!string.Equals(host, "xnode-1", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{}")
                };
            }

            var json = JsonSerializer.Serialize(new
            {
                type = "reward",
                nodeId = new string('0', 63) + "1",
                blsPublicKey = _signedBlsPublicKey,
                amount,
                timestamp = 0,
                msgToSign = "aa",
                signature = "bb"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
