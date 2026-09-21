using XauScalp.Domain;

namespace XauScalp.Risk;

public interface IRiskEngine
{
    RiskDecision Evaluate(
        XauMarketState state,
        XauDecision modelDecision,
        PortfolioState portfolio,
        RiskSettings settings);
}

public sealed record RiskSymbolProfile(
    string Symbol,
    string BrokerSymbol,
    decimal Point,
    decimal TickSize,
    decimal TickValue,
    decimal MinVolume,
    decimal MaxVolume,
    decimal VolumeStep,
    decimal MinStopDistance,
    decimal EstimatedMarginPerLotMoney);

public sealed record RiskPolicyConfiguration(
    string RiskPolicyVersion,
    decimal ProtectiveStopDistancePrice,
    PositionOwnership Ownership);

public interface IRiskRuntimeContextProvider
{
    RiskSymbolProfile GetSymbolProfile(XauMarketState state);
}

public sealed record OwnedRiskLedgerSnapshot(
    DateOnly TradingDay,
    bool IsReady,
    decimal DayStartEquity,
    decimal RealizedNetPnlMoney,
    int ClosedTrades,
    DateTimeOffset? LastLossAtUtc,
    DateTimeOffset? LastExecutionFailureAtUtc);

public interface IOwnedRiskLedger
{
    OwnedRiskLedgerSnapshot GetSnapshot(DateTimeOffset asOfUtc);
}

public sealed class FixedRiskRuntimeContextProvider : IRiskRuntimeContextProvider
{
    private readonly RiskSymbolProfile _profile;

    public FixedRiskRuntimeContextProvider(RiskSymbolProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public RiskSymbolProfile GetSymbolProfile(XauMarketState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return _profile;
    }
}

public sealed class FixedOwnedRiskLedger : IOwnedRiskLedger
{
    private readonly OwnedRiskLedgerSnapshot _snapshot;

    public FixedOwnedRiskLedger(OwnedRiskLedgerSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public OwnedRiskLedgerSnapshot GetSnapshot(DateTimeOffset asOfUtc)
    {
        return _snapshot;
    }
}
