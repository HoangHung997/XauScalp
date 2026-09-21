using System.Text.Json;
using XauScalp.Domain;

namespace XauScalp.Execution.Tests;

public sealed class Mt5DemoExecutionBrokerGatewayTests
{
    private static readonly PositionOwnership Ownership =
        new(991188, "runtime-demo-1", "xauscalp");

    [Fact]
    public async Task SubmitAsync_MapsFilledDemoReplyAndUsesStableBrokerComment()
    {
        TradePlan plan = Plan();
        var transport = new FakeTransport(command => Reply(
            command,
            outcome: "filled",
            brokerOrderId: "1001",
            brokerDealId: "2001",
            brokerPositionId: "3001",
            requestedPrice: plan.PlannedEntryPrice,
            fillPrice: 100.25m,
            requestedVolumeLots: plan.VolumeLots,
            filledVolumeLots: plan.VolumeLots,
            slippagePoints: 5,
            latencyMs: 42));

        var gateway = new Mt5DemoExecutionBrokerGateway(
            Options(),
            transport);

        BrokerExecutionResponse response = await gateway.SubmitAsync(
            plan,
            Ownership,
            CancellationToken.None);

        Assert.Equal(BrokerExecutionOutcome.Filled, response.Outcome);
        Assert.Equal("3001", response.BrokerPositionId);
        Assert.Equal(100.25m, response.FillPrice);
        Assert.Equal(TimeSpan.FromMilliseconds(42), response.Latency);
        Assert.False(response.SafeToRetry);

        Mt5DemoExecutionCommand sent = Assert.Single(transport.Commands);
        Assert.True(sent.DemoOnly);
        Assert.Equal(Mt5DemoExecutionProtocol.SubmitOperation, sent.Operation);
        Assert.Equal(plan.TradeIntentId, sent.TradeIntentId);
        Assert.Equal(Ownership.MagicNumber, sent.MagicNumber);
        Assert.Equal("XAUUSD.G", sent.BrokerSymbol);
        Assert.NotNull(sent.BrokerComment);
        Assert.StartsWith("XS", sent.BrokerComment, StringComparison.Ordinal);
        Assert.Equal(31, sent.BrokerComment!.Length);
        Assert.Equal(
            Mt5DemoExecutionBrokerGateway.BuildBrokerComment(plan.TradeIntentId),
            sent.BrokerComment);
    }

    [Fact]
    public async Task SubmitAsync_NonDemoReplyFailsClosed()
    {
        TradePlan plan = Plan();
        var transport = new FakeTransport(
            command => Reply(command, outcome: "rejected") with
            {
                DemoAccountVerified = false,
            });

        var gateway = new Mt5DemoExecutionBrokerGateway(
            Options(),
            transport);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.SubmitAsync(
                plan,
                Ownership,
                CancellationToken.None));
    }

    [Fact]
    public async Task SubmitAsync_UnknownOutcomeRequiresReconciliation()
    {
        TradePlan plan = Plan();
        var transport = new FakeTransport(
            command => Reply(command, outcome: "unknown") with
            {
                Message = "dispatch result was lost",
            });

        var gateway = new Mt5DemoExecutionBrokerGateway(
            Options(),
            transport);

        await Assert.ThrowsAsync<BrokerOutcomeUnknownException>(
            () => gateway.SubmitAsync(
                plan,
                Ownership,
                CancellationToken.None));
    }

    [Fact]
    public async Task SubmitAsync_SafeRetryIsSuppressedWhenBrokerIdentityExists()
    {
        TradePlan plan = Plan();
        var transport = new FakeTransport(
            command => Reply(command, outcome: "requote") with
            {
                BrokerOrderId = "broker-may-have-order",
                SafeToRetry = true,
            });

        var gateway = new Mt5DemoExecutionBrokerGateway(
            Options(),
            transport);

        BrokerExecutionResponse response = await gateway.SubmitAsync(
            plan,
            Ownership,
            CancellationToken.None);

        Assert.Equal(BrokerExecutionOutcome.Requote, response.Outcome);
        Assert.False(response.SafeToRetry);
    }

    [Fact]
    public async Task QueryState_MapsKnownAndOrphanPositionsWithoutClaimingForeignOwnership()
    {
        Guid knownIntent = Guid.Parse(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        Guid closedIntent = Guid.Parse(
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

        var transport = new FakeTransport(command => Reply(
            command,
            outcome: null) with
        {
            Type = Mt5DemoExecutionProtocol.StateType,
            Operation = Mt5DemoExecutionProtocol.QueryStateOperation,
            TradeIntentId = null,
            Positions =
            [
                new Mt5DemoPositionWire(
                    knownIntent,
                    "position-known",
                    "XAUUSD.G",
                    Mt5DemoExecutionBrokerGateway.BuildBrokerComment(knownIntent),
                    "long",
                    0.10m,
                    2500.00m,
                    2495.00m,
                    2510.00m,
                    Ownership.MagicNumber),
                new Mt5DemoPositionWire(
                    TradeIntentId: null,
                    "position-orphan",
                    "XAUUSD.G",
                    "XSorphan",
                    "short",
                    0.10m,
                    2501.00m,
                    2506.00m,
                    2491.00m,
                    Ownership.MagicNumber),
            ],
            Orders =
            [
                new Mt5DemoOrderWire(
                    knownIntent,
                    "order-known",
                    "XAUUSD.G",
                    Mt5DemoExecutionBrokerGateway.BuildBrokerComment(knownIntent),
                    Ownership.MagicNumber),
            ],
            ClosedTradeIntentIds = [closedIntent],
        });

        var gateway = new Mt5DemoExecutionBrokerGateway(
            Options(),
            transport);

        BrokerReconciliationSnapshot snapshot = await gateway.QueryStateAsync(
            CancellationToken.None);

        Assert.Equal(2, snapshot.Positions.Count);
        Assert.Single(snapshot.Orders);
        Assert.Contains(closedIntent, snapshot.ClosedTradeIntentIds);

        BrokerPositionSnapshot known = snapshot.Positions.Single(
            item => item.BrokerPositionId == "position-known");
        Assert.Equal(knownIntent, known.TradeIntentId);
        Assert.Equal(Ownership, known.Ownership);

        BrokerPositionSnapshot orphan = snapshot.Positions.Single(
            item => item.BrokerPositionId == "position-orphan");
        Assert.NotEqual(Guid.Empty, orphan.TradeIntentId);
        Assert.NotEqual(knownIntent, orphan.TradeIntentId);
        Assert.Equal(Ownership, orphan.Ownership);
    }

    [Fact]
    public async Task QueryDemoContext_MapsBrokerAccountAndSymbolRiskForHardRisk()
    {
        DateTimeOffset now = Utc(12, 0, 0);
        Guid intent = Guid.Parse(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

        var transport = new FakeTransport(command => Reply(
            command,
            outcome: null) with
        {
            Type = Mt5DemoExecutionProtocol.StateType,
            Operation = Mt5DemoExecutionProtocol.QueryStateOperation,
            TradeIntentId = null,
            Positions =
            [
                new Mt5DemoPositionWire(
                    intent,
                    "position-1",
                    "XAUUSD.G",
                    Mt5DemoExecutionBrokerGateway.BuildBrokerComment(intent),
                    "long",
                    0.10m,
                    2500.00m,
                    2495.00m,
                    2510.00m,
                    Ownership.MagicNumber,
                    CurrentPrice: 2501.00m,
                    UnrealizedPnlMoney: 10m,
                    OpenedAtUnixMs: now.AddMinutes(-5)
                        .ToUnixTimeMilliseconds()),
            ],
            Account = new Mt5DemoAccountWire(
                Balance: 10_000m,
                Equity: 10_010m,
                FreeMargin: 8_500m),
            SymbolRisk = new Mt5DemoSymbolRiskWire(
                Point: 0.01m,
                TickSize: 0.01m,
                TickValue: 1m,
                MinVolume: 0.01m,
                MaxVolume: 100m,
                VolumeStep: 0.01m,
                MinStopDistance: 0.50m,
                EstimatedMarginPerLotMoney: 500m),
        });

        var gateway = new Mt5DemoExecutionBrokerGateway(
            Options(),
            transport,
            new FixedTimeProvider(now));

        Mt5DemoBrokerContextSnapshot context =
            await gateway.QueryDemoContextAsync(
                CancellationToken.None);

        Assert.Equal(now, context.Portfolio.AsOfUtc);
        Assert.Equal(10_000m, context.Portfolio.Balance);
        Assert.Equal(10_010m, context.Portfolio.Equity);
        Assert.Equal(8_500m, context.Portfolio.FreeMargin);

        PositionState position = Assert.Single(
            context.Portfolio.Positions);
        Assert.Equal(intent, position.TradeIntentId);
        Assert.Equal("position-1", position.BrokerPositionId);
        Assert.Equal(2501m, position.CurrentPrice);
        Assert.Equal(10m, position.UnrealizedPnlMoney);
        Assert.Equal(
            now.AddMinutes(-5),
            position.OpenedAtUtc);

        Assert.Equal(0.01m, context.SymbolRisk.TickSize);
        Assert.Equal(1m, context.SymbolRisk.TickValue);
        Assert.Equal(
            500m,
            context.SymbolRisk.EstimatedMarginPerLotMoney);

        Assert.Single(context.Reconciliation.Positions);
    }

    [Fact]
    public async Task SubmitAsync_OwnershipMismatchDoesNotReachTransport()
    {
        var transport = new FakeTransport(
            command => Reply(command, outcome: "rejected"));
        var gateway = new Mt5DemoExecutionBrokerGateway(
            Options(),
            transport);

        var foreign = new PositionOwnership(
            Ownership.MagicNumber,
            "other-runtime",
            Ownership.StrategyId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.SubmitAsync(
                Plan(),
                foreign,
                CancellationToken.None));

        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task FileTransport_RequiresFreshVerifiedDemoHeartbeatBeforeCommand()
    {
        string directory = TempDirectory();
        try
        {
            DateTimeOffset now = Utc(12, 0, 0);
            Mt5DemoExecutionGatewayOptions options = Options(directory);
            WriteBridgeLifecycle(
                options.EventFilePath,
                sessionId: "session-live-rejected",
                now,
                demoVerified: false);

            var transport = new Mt5DemoFileExecutionTransport(
                options,
                new FixedTimeProvider(now));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => transport.ExchangeAsync(
                    Command(Guid.NewGuid()),
                    CancellationToken.None));

            Assert.False(File.Exists(options.CommandFilePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileTransport_RejectsStaleHeartbeatBeforeAppendingCommand()
    {
        string directory = TempDirectory();
        try
        {
            DateTimeOffset now = Utc(12, 0, 10);
            Mt5DemoExecutionGatewayOptions options = Options(directory);
            WriteBridgeLifecycle(
                options.EventFilePath,
                sessionId: "session-stale",
                now.AddSeconds(-10),
                demoVerified: true);

            var transport = new Mt5DemoFileExecutionTransport(
                options,
                new FixedTimeProvider(now));

            await Assert.ThrowsAsync<BrokerSafeToRetryException>(
                () => transport.ExchangeAsync(
                    Command(Guid.NewGuid()),
                    CancellationToken.None));

            Assert.False(File.Exists(options.CommandFilePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileTransport_BindsCommandToActiveSessionAndReadsFinalReply()
    {
        string directory = TempDirectory();
        try
        {
            DateTimeOffset now = Utc(12, 0, 0);
            Mt5DemoExecutionGatewayOptions options = Options(directory);
            const string sessionId = "session-demo-1";
            WriteBridgeLifecycle(
                options.EventFilePath,
                sessionId,
                now,
                demoVerified: true);

            Guid commandId = Guid.Parse(
                "cccccccc-cccc-4ccc-8ccc-cccccccccccc");
            Mt5DemoExecutionCommand command = Command(commandId);
            Mt5DemoExecutionReply reply = Reply(
                command with { BridgeSessionId = sessionId },
                outcome: "rejected");

            File.AppendAllText(
                options.EventFilePath,
                JsonSerializer.Serialize(
                    reply,
                    JsonOptions()) + "\n");

            var transport = new Mt5DemoFileExecutionTransport(
                options,
                new FixedTimeProvider(now));

            Mt5DemoExecutionReply actual = await transport.ExchangeAsync(
                command,
                CancellationToken.None);

            Assert.Equal(commandId, actual.CommandId);
            Assert.Equal(sessionId, actual.BridgeSessionId);

            string written = Assert.Single(
                File.ReadAllLines(options.CommandFilePath));
            using JsonDocument document = JsonDocument.Parse(written);
            Assert.Equal(
                sessionId,
                document.RootElement
                    .GetProperty("bridgeSessionId")
                    .GetString());
            Assert.True(
                document.RootElement
                    .GetProperty("demoOnly")
                    .GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Mt5DemoExecutionGatewayOptions Options(
        string? directory = null)
    {
        string root = directory ?? Path.GetTempPath();
        return new Mt5DemoExecutionGatewayOptions(
            Path.Combine(root, "xauscalp-test-commands.ndjson"),
            Path.Combine(root, "xauscalp-test-events.ndjson"),
            "XAUUSD",
            "XAUUSD.G",
            Ownership,
            maxSlippagePoints: 30,
            commandTimeout: TimeSpan.FromMilliseconds(250),
            pollInterval: TimeSpan.FromMilliseconds(10),
            maxHeartbeatAge: TimeSpan.FromSeconds(5));
    }

    private static Mt5DemoExecutionCommand Command(Guid commandId)
    {
        return new Mt5DemoExecutionCommand(
            Mt5DemoExecutionProtocol.Version,
            commandId,
            string.Empty,
            Mt5DemoExecutionProtocol.QueryStateOperation,
            TradeIntentId: null,
            "XAUUSD.G",
            Ownership.MagicNumber,
            BrokerComment: null,
            BrokerPositionId: null,
            Side: null,
            VolumeLots: null,
            PlannedEntryPrice: null,
            StopLossPrice: null,
            TakeProfitPrice: null,
            MaxSlippagePoints: 30,
            DemoOnly: true);
    }

    private static Mt5DemoExecutionReply Reply(
        Mt5DemoExecutionCommand command,
        string? outcome,
        string? brokerOrderId = null,
        string? brokerDealId = null,
        string? brokerPositionId = null,
        decimal? requestedPrice = null,
        decimal? fillPrice = null,
        decimal requestedVolumeLots = 0m,
        decimal filledVolumeLots = 0m,
        double? slippagePoints = null,
        double latencyMs = 1d)
    {
        return new Mt5DemoExecutionReply(
            Mt5DemoExecutionProtocol.Version,
            Mt5DemoExecutionProtocol.ExecutionResultType,
            command.CommandId,
            string.IsNullOrWhiteSpace(command.BridgeSessionId)
                ? "fake-session"
                : command.BridgeSessionId,
            DemoAccountVerified: true,
            command.Operation,
            command.TradeIntentId,
            outcome,
            brokerOrderId,
            brokerDealId,
            brokerPositionId,
            requestedPrice,
            fillPrice,
            requestedVolumeLots,
            filledVolumeLots,
            slippagePoints,
            latencyMs,
            BrokerRetcode: "TEST",
            Message: null,
            SafeToRetry: false,
            Positions: null,
            Orders: null,
            ClosedTradeIntentIds: null);
    }

    private static void WriteBridgeLifecycle(
        string path,
        string sessionId,
        DateTimeOffset heartbeatAt,
        bool demoVerified)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        object ready = new
        {
            protocolVersion = Mt5DemoExecutionProtocol.Version,
            type = Mt5DemoExecutionProtocol.BridgeReadyType,
            bridgeSessionId = sessionId,
            demoAccountVerified = demoVerified,
            brokerSymbol = "XAUUSD.G",
            magicNumber = Ownership.MagicNumber,
        };

        object heartbeat = new
        {
            protocolVersion = Mt5DemoExecutionProtocol.Version,
            type = Mt5DemoExecutionProtocol.BridgeHeartbeatType,
            bridgeSessionId = sessionId,
            demoAccountVerified = demoVerified,
            brokerSymbol = "XAUUSD.G",
            magicNumber = Ownership.MagicNumber,
            utcUnixMs = heartbeatAt.ToUnixTimeMilliseconds(),
        };

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(ready, JsonOptions())
            + "\n"
            + JsonSerializer.Serialize(heartbeat, JsonOptions())
            + "\n");
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    private static string TempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "xauscalp-mt5-demo-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static TradePlan Plan()
    {
        return new TradePlan(
            ContractVersions.TradePlanV1,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            "XAUUSD",
            "XAUUSD.G",
            TradeSide.Long,
            0.10m,
            2500.20m,
            2495.20m,
            2510.20m,
            "risk-v1",
            "settings-v1",
            Utc(11, 59, 59));
    }

    private static DateTimeOffset Utc(
        int hour,
        int minute,
        int second)
    {
        return new DateTimeOffset(
            2026,
            9,
            21,
            hour,
            minute,
            second,
            TimeSpan.Zero);
    }

    private sealed class FakeTransport : IMt5DemoExecutionTransport
    {
        private readonly Func<
            Mt5DemoExecutionCommand,
            Mt5DemoExecutionReply> _replyFactory;

        public FakeTransport(
            Func<Mt5DemoExecutionCommand, Mt5DemoExecutionReply> replyFactory)
        {
            _replyFactory = replyFactory;
        }

        public List<Mt5DemoExecutionCommand> Commands { get; } = [];

        public Task<Mt5DemoExecutionReply> ExchangeAsync(
            Mt5DemoExecutionCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(_replyFactory(command));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
