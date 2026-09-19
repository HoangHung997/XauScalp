using System.Text.Json.Serialization;

namespace XauScalp.Domain;

public enum LiquiditySource
{
    None = 0,
    Estimated = 1,
    RealDom = 2,
}

public sealed record NumericFeatureValue
{
    [JsonConstructor]
    public NumericFeatureValue(
        string name,
        double? value,
        string unit,
        bool isAvailable,
        DateTimeOffset? observedAtUtc,
        string? unavailableReason)
    {
        Name = ContractGuard.Required(name, nameof(name));
        Unit = ContractGuard.Required(unit, nameof(unit));
        IsAvailable = isAvailable;

        if (isAvailable)
        {
            if (value is null || !double.IsFinite(value.Value))
            {
                throw new ArgumentException("Available numeric features require a finite value.", nameof(value));
            }

            if (!string.IsNullOrWhiteSpace(unavailableReason))
            {
                throw new ArgumentException("Available features cannot carry an unavailable reason.", nameof(unavailableReason));
            }
        }
        else
        {
            if (value is not null)
            {
                throw new ArgumentException("Unavailable features must not carry a numeric value.", nameof(value));
            }

            ContractGuard.Required(unavailableReason, nameof(unavailableReason));
        }

        if (observedAtUtc is not null)
        {
            ContractGuard.Utc(observedAtUtc.Value, nameof(observedAtUtc));
        }

        Value = value;
        ObservedAtUtc = observedAtUtc;
        UnavailableReason = unavailableReason;
    }

    public string Name { get; }

    public double? Value { get; }

    public string Unit { get; }

    public bool IsAvailable { get; }

    public DateTimeOffset? ObservedAtUtc { get; }

    public string? UnavailableReason { get; }
}

public sealed record DataReadiness
{
    [JsonConstructor]
    public DataReadiness(
        bool requiredP0Ready,
        bool tickHistoryReady,
        bool barHistoryReady,
        bool newsDataAvailable,
        string[] missingRequirements)
    {
        RequiredP0Ready = requiredP0Ready;
        TickHistoryReady = tickHistoryReady;
        BarHistoryReady = barHistoryReady;
        NewsDataAvailable = newsDataAvailable;
        MissingRequirements = missingRequirements?.ToArray() ?? throw new ArgumentNullException(nameof(missingRequirements));
    }

    public bool RequiredP0Ready { get; }

    public bool TickHistoryReady { get; }

    public bool BarHistoryReady { get; }

    public bool NewsDataAvailable { get; }

    public string[] MissingRequirements { get; }
}

public sealed record XauMarketState
{
    [JsonConstructor]
    public XauMarketState(
        string contractVersion,
        Guid marketStateId,
        DateTimeOffset timestampUtc,
        DateTimeOffset? brokerTimestamp,
        long sequenceId,
        string symbol,
        string brokerSymbol,
        decimal bid,
        decimal ask,
        decimal mid,
        string featureSchemaVersion,
        string dataSourceId,
        LiquiditySource liquiditySource,
        DataReadiness readiness,
        NumericFeatureValue[] features)
    {
        ContractVersion = ContractGuard.ExactVersion(contractVersion, ContractVersions.MarketStateV1, nameof(contractVersion));
        MarketStateId = ContractGuard.NonEmpty(marketStateId, nameof(marketStateId));
        TimestampUtc = ContractGuard.Utc(timestampUtc, nameof(timestampUtc));
        BrokerTimestamp = brokerTimestamp;
        SequenceId = ContractGuard.NonNegative(sequenceId, nameof(sequenceId));
        Symbol = ContractGuard.Required(symbol, nameof(symbol));
        BrokerSymbol = ContractGuard.Required(brokerSymbol, nameof(brokerSymbol));
        Bid = ContractGuard.Positive(bid, nameof(bid));
        Ask = ContractGuard.Positive(ask, nameof(ask));
        Mid = ContractGuard.Positive(mid, nameof(mid));
        FeatureSchemaVersion = ContractGuard.Required(featureSchemaVersion, nameof(featureSchemaVersion));
        DataSourceId = ContractGuard.Required(dataSourceId, nameof(dataSourceId));
        LiquiditySource = liquiditySource;
        Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        Features = features?.ToArray() ?? throw new ArgumentNullException(nameof(features));

        if (ask < bid)
        {
            throw new ArgumentException("Ask must be greater than or equal to bid.", nameof(ask));
        }

        if (mid < bid || mid > ask)
        {
            throw new ArgumentOutOfRangeException(nameof(mid), mid, "Mid must be between bid and ask.");
        }

        if (Features.Select(static feature => feature.Name).Distinct(StringComparer.Ordinal).Count() != Features.Length)
        {
            throw new ArgumentException("Feature names must be unique within one market state.", nameof(features));
        }
    }

    public string ContractVersion { get; }

    public Guid MarketStateId { get; }

    public DateTimeOffset TimestampUtc { get; }

    public DateTimeOffset? BrokerTimestamp { get; }

    public long SequenceId { get; }

    public string Symbol { get; }

    public string BrokerSymbol { get; }

    public decimal Bid { get; }

    public decimal Ask { get; }

    public decimal Mid { get; }

    public string FeatureSchemaVersion { get; }

    public string DataSourceId { get; }

    public LiquiditySource LiquiditySource { get; }

    public DataReadiness Readiness { get; }

    public NumericFeatureValue[] Features { get; }
}
