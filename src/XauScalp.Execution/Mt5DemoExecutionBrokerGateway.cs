using System.Security.Cryptography;
using System.Text;
using XauScalp.Domain;

namespace XauScalp.Execution;

public sealed class Mt5DemoExecutionBrokerGateway : IExecutionBrokerGateway
{
    private readonly Mt5DemoExecutionGatewayOptions _options;
    private readonly IMt5DemoExecutionTransport _transport;

    public Mt5DemoExecutionBrokerGateway(
        Mt5DemoExecutionGatewayOptions options,
        IMt5DemoExecutionTransport? transport = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transport = transport
            ?? new Mt5DemoFileExecutionTransport(_options, timeProvider);
    }

    public async Task<BrokerExecutionResponse> SubmitAsync(
        TradePlan plan,
        PositionOwnership ownership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureOwnership(ownership);
        EnsureSymbol(plan.Symbol, plan.BrokerSymbol);

        Guid commandId = Guid.NewGuid();
        var command = new Mt5DemoExecutionCommand(
            Mt5DemoExecutionProtocol.Version,
            commandId,
            string.Empty,
            Mt5DemoExecutionProtocol.SubmitOperation,
            plan.TradeIntentId,
            plan.BrokerSymbol,
            _options.Ownership.MagicNumber,
            BuildBrokerComment(plan.TradeIntentId),
            BrokerPositionId: null,
            SideText(plan.Side),
            plan.VolumeLots,
            plan.PlannedEntryPrice,
            plan.ProtectiveStopPrice,
            plan.TakeProfitPrice,
            _options.MaxSlippagePoints,
            DemoOnly: true);

        Mt5DemoExecutionReply reply = await _transport
            .ExchangeAsync(command, cancellationToken)
            .ConfigureAwait(false);

        return MapExecutionReply(
            command,
            reply,
            requirePositionIdForFilledSubmit: true);
    }

    public async Task<BrokerExecutionResponse> ModifyAsync(
        PositionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureOwnership(command.Ownership);
        RequirePositionId(command.BrokerPositionId);

        var wireCommand = new Mt5DemoExecutionCommand(
            Mt5DemoExecutionProtocol.Version,
            Guid.NewGuid(),
            string.Empty,
            Mt5DemoExecutionProtocol.ModifyOperation,
            command.TradeIntentId,
            _options.BrokerSymbol,
            _options.Ownership.MagicNumber,
            BuildBrokerComment(command.TradeIntentId),
            command.BrokerPositionId,
            Side: null,
            VolumeLots: null,
            PlannedEntryPrice: null,
            command.StopLossPrice,
            command.TakeProfitPrice,
            _options.MaxSlippagePoints,
            DemoOnly: true);

        Mt5DemoExecutionReply reply = await _transport
            .ExchangeAsync(wireCommand, cancellationToken)
            .ConfigureAwait(false);

        return MapExecutionReply(
            wireCommand,
            reply,
            requirePositionIdForFilledSubmit: false);
    }

    public async Task<BrokerExecutionResponse> CloseAsync(
        PositionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureOwnership(command.Ownership);
        RequirePositionId(command.BrokerPositionId);

        var wireCommand = new Mt5DemoExecutionCommand(
            Mt5DemoExecutionProtocol.Version,
            Guid.NewGuid(),
            string.Empty,
            Mt5DemoExecutionProtocol.CloseOperation,
            command.TradeIntentId,
            _options.BrokerSymbol,
            _options.Ownership.MagicNumber,
            BuildBrokerComment(command.TradeIntentId),
            command.BrokerPositionId,
            Side: null,
            VolumeLots: null,
            PlannedEntryPrice: null,
            StopLossPrice: null,
            TakeProfitPrice: null,
            _options.MaxSlippagePoints,
            DemoOnly: true);

        Mt5DemoExecutionReply reply = await _transport
            .ExchangeAsync(wireCommand, cancellationToken)
            .ConfigureAwait(false);

        return MapExecutionReply(
            wireCommand,
            reply,
            requirePositionIdForFilledSubmit: false);
    }

    public async Task<BrokerReconciliationSnapshot> QueryStateAsync(
        CancellationToken cancellationToken)
    {
        var command = new Mt5DemoExecutionCommand(
            Mt5DemoExecutionProtocol.Version,
            Guid.NewGuid(),
            string.Empty,
            Mt5DemoExecutionProtocol.QueryStateOperation,
            TradeIntentId: null,
            _options.BrokerSymbol,
            _options.Ownership.MagicNumber,
            BrokerComment: null,
            BrokerPositionId: null,
            Side: null,
            VolumeLots: null,
            PlannedEntryPrice: null,
            StopLossPrice: null,
            TakeProfitPrice: null,
            _options.MaxSlippagePoints,
            DemoOnly: true);

        Mt5DemoExecutionReply reply = await _transport
            .ExchangeAsync(command, cancellationToken)
            .ConfigureAwait(false);

        ValidateCommonReply(command, reply);

        if (!string.Equals(
                reply.Type,
                Mt5DemoExecutionProtocol.StateType,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"MT5 demo bridge returned '{reply.Type}' for a state query.");
        }

        if (!string.Equals(
                reply.Operation,
                Mt5DemoExecutionProtocol.QueryStateOperation,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "MT5 demo bridge state response operation does not match queryState.");
        }

        BrokerPositionSnapshot[] positions = (reply.Positions ?? Array.Empty<Mt5DemoPositionWire>())
            .Select(MapPosition)
            .ToArray();

        BrokerOrderSnapshot[] orders = (reply.Orders ?? Array.Empty<Mt5DemoOrderWire>())
            .Select(MapOrder)
            .ToArray();

        HashSet<Guid> closed = (reply.ClosedTradeIntentIds ?? Array.Empty<Guid>())
            .Select(RequireNonEmptyTradeIntentId)
            .ToHashSet();

        return new BrokerReconciliationSnapshot(
            positions,
            orders,
            closed);
    }

    public static string BuildBrokerComment(Guid tradeIntentId)
    {
        if (tradeIntentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Trade intent id must be non-empty.",
                nameof(tradeIntentId));
        }

        string compact = tradeIntentId.ToString("N");
        return "XS" + compact[..29];
    }

    private BrokerExecutionResponse MapExecutionReply(
        Mt5DemoExecutionCommand command,
        Mt5DemoExecutionReply reply,
        bool requirePositionIdForFilledSubmit)
    {
        ValidateCommonReply(command, reply);

        if (!string.Equals(
                reply.Type,
                Mt5DemoExecutionProtocol.ExecutionResultType,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"MT5 demo bridge returned '{reply.Type}' for operation '{command.Operation}'.");
        }

        if (!string.Equals(reply.Operation, command.Operation, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "MT5 demo bridge response operation does not match the submitted command.");
        }

        if (command.TradeIntentId is not Guid expectedIntent
            || reply.TradeIntentId != expectedIntent)
        {
            throw new InvalidDataException(
                "MT5 demo bridge response trade intent does not match the submitted command.");
        }

        string outcomeText = reply.Outcome
            ?? throw new InvalidDataException(
                "MT5 demo execution response is missing outcome.");

        if (string.Equals(outcomeText, "unknown", StringComparison.Ordinal))
        {
            throw new BrokerOutcomeUnknownException(
                reply.Message
                ?? $"MT5 demo bridge reported unknown outcome for command {command.CommandId}.");
        }

        BrokerExecutionOutcome outcome = outcomeText switch
        {
            "accepted" => BrokerExecutionOutcome.Accepted,
            "partiallyFilled" => BrokerExecutionOutcome.PartiallyFilled,
            "filled" => BrokerExecutionOutcome.Filled,
            "rejected" => BrokerExecutionOutcome.Rejected,
            "requote" => BrokerExecutionOutcome.Requote,
            _ => throw new InvalidDataException(
                $"Unsupported MT5 demo bridge outcome '{outcomeText}'."),
        };

        if (reply.RequestedVolumeLots < 0
            || reply.FilledVolumeLots < 0
            || reply.FilledVolumeLots > reply.RequestedVolumeLots)
        {
            throw new InvalidDataException(
                "MT5 demo bridge returned invalid requested/filled volume.");
        }

        if (!double.IsFinite(reply.LatencyMs) || reply.LatencyMs < 0)
        {
            throw new InvalidDataException(
                "MT5 demo bridge returned invalid latency.");
        }

        if (reply.SlippagePoints is double slippage
            && !double.IsFinite(slippage))
        {
            throw new InvalidDataException(
                "MT5 demo bridge returned non-finite slippage.");
        }

        if (requirePositionIdForFilledSubmit
            && outcome == BrokerExecutionOutcome.Filled
            && string.IsNullOrWhiteSpace(reply.BrokerPositionId))
        {
            throw new InvalidDataException(
                "Filled MT5 demo submit did not return a broker position id.");
        }

        bool safeToRetry = reply.SafeToRetry
            && (outcome is BrokerExecutionOutcome.Rejected
                or BrokerExecutionOutcome.Requote)
            && string.IsNullOrWhiteSpace(reply.BrokerOrderId)
            && string.IsNullOrWhiteSpace(reply.BrokerDealId)
            && string.IsNullOrWhiteSpace(reply.BrokerPositionId);

        return new BrokerExecutionResponse(
            outcome,
            EmptyToNull(reply.BrokerOrderId),
            EmptyToNull(reply.BrokerDealId),
            EmptyToNull(reply.BrokerPositionId),
            reply.RequestedPrice,
            reply.FillPrice,
            reply.RequestedVolumeLots,
            reply.FilledVolumeLots,
            reply.SlippagePoints,
            TimeSpan.FromMilliseconds(reply.LatencyMs),
            EmptyToNull(reply.BrokerRetcode),
            reply.Message,
            safeToRetry);
    }

    private BrokerPositionSnapshot MapPosition(Mt5DemoPositionWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);

        ValidateBrokerIdentity(
            wire.BrokerSymbol,
            wire.MagicNumber,
            "position");

        string brokerPositionId = RequirePositionId(wire.BrokerPositionId);
        Guid tradeIntentId = wire.TradeIntentId is Guid known
            && known != Guid.Empty
            ? known
            : DeterministicOrphanId(
                "position",
                brokerPositionId,
                wire.BrokerComment);

        TradeSide side = ParseSide(wire.Side);

        if (wire.VolumeLots <= 0 || wire.EntryPrice <= 0)
        {
            throw new InvalidDataException(
                $"MT5 demo broker position {brokerPositionId} has invalid volume/entry price.");
        }

        if (wire.StopLossPrice is <= 0 || wire.TakeProfitPrice is <= 0)
        {
            throw new InvalidDataException(
                $"MT5 demo broker position {brokerPositionId} has invalid protection prices.");
        }

        return new BrokerPositionSnapshot(
            tradeIntentId,
            brokerPositionId,
            _options.CanonicalSymbol,
            _options.BrokerSymbol,
            side,
            wire.VolumeLots,
            wire.EntryPrice,
            wire.StopLossPrice,
            wire.TakeProfitPrice,
            _options.Ownership);
    }

    private BrokerOrderSnapshot MapOrder(Mt5DemoOrderWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);

        ValidateBrokerIdentity(
            wire.BrokerSymbol,
            wire.MagicNumber,
            "order");

        string brokerOrderId = string.IsNullOrWhiteSpace(wire.BrokerOrderId)
            ? throw new InvalidDataException(
                "MT5 demo broker order id is required.")
            : wire.BrokerOrderId;

        Guid tradeIntentId = wire.TradeIntentId is Guid known
            && known != Guid.Empty
            ? known
            : DeterministicOrphanId(
                "order",
                brokerOrderId,
                wire.BrokerComment);

        return new BrokerOrderSnapshot(
            tradeIntentId,
            brokerOrderId,
            _options.CanonicalSymbol,
            _options.BrokerSymbol,
            _options.Ownership);
    }

    private void ValidateCommonReply(
        Mt5DemoExecutionCommand command,
        Mt5DemoExecutionReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        if (!string.Equals(
                reply.ProtocolVersion,
                Mt5DemoExecutionProtocol.Version,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"MT5 demo bridge replied with unsupported protocol '{reply.ProtocolVersion}'.");
        }

        if (reply.CommandId != command.CommandId)
        {
            throw new InvalidDataException(
                "MT5 demo bridge response command id does not match request.");
        }

        if (!reply.DemoAccountVerified)
        {
            throw new InvalidOperationException(
                "MT5 bridge response is not verified as demo. Real-money execution is forbidden.");
        }

        if (string.IsNullOrWhiteSpace(reply.BridgeSessionId))
        {
            throw new InvalidDataException(
                "MT5 demo bridge response is missing bridge session id.");
        }
    }

    private void EnsureSymbol(
        string canonicalSymbol,
        string brokerSymbol)
    {
        if (!string.Equals(
                canonicalSymbol,
                _options.CanonicalSymbol,
                StringComparison.Ordinal)
            || !string.Equals(
                brokerSymbol,
                _options.BrokerSymbol,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Trade plan symbol/broker symbol does not match MT5 demo gateway configuration.");
        }
    }

    private void EnsureOwnership(PositionOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        if (ownership.MagicNumber != _options.Ownership.MagicNumber
            || !string.Equals(
                ownership.RuntimeInstanceId,
                _options.Ownership.RuntimeInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                ownership.StrategyId,
                _options.Ownership.StrategyId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Execution ownership does not match MT5 demo gateway ownership.");
        }
    }

    private void ValidateBrokerIdentity(
        string brokerSymbol,
        long magicNumber,
        string kind)
    {
        if (!string.Equals(
                brokerSymbol,
                _options.BrokerSymbol,
                StringComparison.Ordinal)
            || magicNumber != _options.Ownership.MagicNumber)
        {
            throw new InvalidDataException(
                $"MT5 demo {kind} does not match configured broker symbol/ownership.");
        }
    }

    private static string SideText(TradeSide side)
    {
        return side switch
        {
            TradeSide.Long => "long",
            TradeSide.Short => "short",
            _ => throw new InvalidDataException(
                $"Unsupported trade side '{side}'."),
        };
    }

    private static TradeSide ParseSide(string side)
    {
        return side switch
        {
            "long" => TradeSide.Long,
            "short" => TradeSide.Short,
            _ => throw new InvalidDataException(
                $"Unsupported MT5 demo position side '{side}'."),
        };
    }

    private static string RequirePositionId(string brokerPositionId)
    {
        return string.IsNullOrWhiteSpace(brokerPositionId)
            ? throw new ArgumentException(
                "Broker position id is required.",
                nameof(brokerPositionId))
            : brokerPositionId;
    }

    private static Guid RequireNonEmptyTradeIntentId(Guid tradeIntentId)
    {
        if (tradeIntentId == Guid.Empty)
        {
            throw new InvalidDataException(
                "MT5 demo closed trade intent id cannot be empty.");
        }

        return tradeIntentId;
    }

    private Guid DeterministicOrphanId(
        string kind,
        string brokerId,
        string? brokerComment)
    {
        string identity = string.Join(
            "|",
            "mt5-demo-orphan-v1",
            kind,
            _options.BrokerSymbol,
            _options.Ownership.MagicNumber.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            brokerId,
            brokerComment ?? string.Empty);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
