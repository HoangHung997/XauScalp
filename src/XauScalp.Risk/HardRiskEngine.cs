using System.Security.Cryptography;
using System.Text;
using XauScalp.Domain;

namespace XauScalp.Risk;

public sealed class HardRiskEngine : IRiskEngine
{
    private const string SpreadAtrRatioFeature = "SpreadAtrRatio";
    private const string HighImpactNewsFeature = "IsHighImpactNewsWindow";

    private readonly IRiskRuntimeContextProvider _contextProvider;
    private readonly IOwnedRiskLedger _ledger;
    private readonly RiskPolicyConfiguration _policy;
    private readonly TimeProvider _timeProvider;

    public HardRiskEngine(
        IRiskRuntimeContextProvider contextProvider,
        IOwnedRiskLedger ledger,
        RiskPolicyConfiguration policy,
        TimeProvider? timeProvider = null)
    {
        _contextProvider = contextProvider
            ?? throw new ArgumentNullException(nameof(contextProvider));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(policy.RiskPolicyVersion))
        {
            throw new ArgumentException(
                "Risk policy version is required.",
                nameof(policy));
        }

        if (policy.ProtectiveStopDistancePrice <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.ProtectiveStopDistancePrice,
                "Protective stop distance must be positive.");
        }
    }

    public RiskDecision Evaluate(
        XauMarketState state,
        XauDecision modelDecision,
        PortfolioState portfolio,
        RiskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(modelDecision);
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(settings);

        DateTimeOffset now = _timeProvider.GetUtcNow();

        RiskDecision? rejected = ValidateInputs(
            state,
            modelDecision,
            portfolio,
            settings,
            now);
        if (rejected is not null)
        {
            return rejected;
        }

        RiskSymbolProfile profile;
        OwnedRiskLedgerSnapshot ledger;

        try
        {
            profile = _contextProvider.GetSymbolProfile(state);
            ledger = _ledger.GetSnapshot(now);
        }
        catch (Exception exception)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "risk-context-unavailable",
                exception.GetType().Name);
        }

        rejected = ValidateProfileAndLedger(
            state,
            modelDecision,
            portfolio,
            settings,
            profile,
            ledger,
            now);
        if (rejected is not null)
        {
            return rejected;
        }

        TradeSide side = modelDecision.Action == TradeAction.Long
            ? TradeSide.Long
            : TradeSide.Short;

        decimal entryPrice = side == TradeSide.Long
            ? state.Ask
            : state.Bid;

        decimal stopDistance = _policy.ProtectiveStopDistancePrice;
        decimal stopPrice = side == TradeSide.Long
            ? entryPrice - stopDistance
            : entryPrice + stopDistance;

        if (stopPrice <= 0)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "invalid-protective-stop",
                "Protective stop price is not positive.");
        }

        if (stopDistance < profile.MinStopDistance)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "min-stop-distance",
                "Configured protective stop is inside the broker minimum stop distance.");
        }

        decimal ticksToStop = stopDistance / profile.TickSize;
        decimal lossPerLot = ticksToStop * profile.TickValue;
        if (ticksToStop <= 0 || lossPerLot <= 0)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "invalid-tick-value",
                "Tick-size/value conversion produced a non-positive loss per lot.");
        }

        decimal configuredRiskMoney =
            portfolio.Equity * (decimal)(settings.MaxRiskPerTradePct / 100.0);

        decimal rawLots = configuredRiskMoney / lossPerLot;
        decimal lots = FloorToStep(
            Math.Min(rawLots, profile.MaxVolume),
            profile.VolumeStep);

        if (lots < profile.MinVolume || lots <= 0)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "below-min-volume",
                "Monetary risk sizing is below the broker minimum volume.");
        }

        decimal actualRiskMoney = lots * lossPerLot;
        decimal requiredMargin = lots * profile.EstimatedMarginPerLotMoney;
        decimal minimumFreeMarginMoney =
            portfolio.Equity * (decimal)(settings.MinFreeMarginPct / 100.0);

        if (requiredMargin > portfolio.FreeMargin
            || portfolio.FreeMargin - requiredMargin < minimumFreeMarginMoney)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "insufficient-free-margin",
                "Estimated post-entry free margin would violate the configured minimum.");
        }

        return new RiskDecision(
            ContractVersions.RiskDecisionV1,
            DeterministicRiskDecisionId(
                state.MarketStateId,
                modelDecision.DecisionId,
                _policy.RiskPolicyVersion),
            state.MarketStateId,
            modelDecision.DecisionId,
            RiskDecisionOutcome.Authorized,
            "authorized",
            reason: null,
            lots,
            actualRiskMoney,
            stopPrice,
            _policy.RiskPolicyVersion,
            now);
    }

    private RiskDecision? ValidateInputs(
        XauMarketState state,
        XauDecision modelDecision,
        PortfolioState portfolio,
        RiskSettings settings,
        DateTimeOffset now)
    {
        if (modelDecision.MarketStateId != state.MarketStateId
            || !string.Equals(
                modelDecision.FeatureSchemaVersion,
                state.FeatureSchemaVersion,
                StringComparison.Ordinal))
        {
            return Reject(
                state,
                modelDecision,
                now,
                "decision-state-mismatch",
                "Decision does not match the supplied market state/schema.");
        }

        if (modelDecision.Action == TradeAction.Wait)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "model-wait",
                "Wait decisions cannot be authorized as trades.");
        }

        if (!state.Readiness.RequiredP0Ready)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "state-not-ready",
                "Required P0 market data is not ready.");
        }

        if (now < state.TimestampUtc
            || now - state.TimestampUtc
                > TimeSpan.FromMilliseconds(settings.MaxFeatureAgeMs))
        {
            return Reject(
                state,
                modelDecision,
                now,
                "state-stale",
                "Market state is stale or from the future.");
        }

        if (now < modelDecision.EvaluatedAtUtc
            || now - modelDecision.EvaluatedAtUtc
                > TimeSpan.FromMilliseconds(settings.MaxDecisionAgeMs))
        {
            return Reject(
                state,
                modelDecision,
                now,
                "decision-stale",
                "Model decision is stale or from the future.");
        }

        if (now < portfolio.AsOfUtc
            || now - portfolio.AsOfUtc
                > TimeSpan.FromMilliseconds(settings.MaxFeatureAgeMs))
        {
            return Reject(
                state,
                modelDecision,
                now,
                "portfolio-stale",
                "Portfolio snapshot is stale or from the future.");
        }

        if (modelDecision.Action == TradeAction.Long && !settings.AllowLong)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "long-disabled",
                "Long entries are disabled by hard risk settings.");
        }

        if (modelDecision.Action == TradeAction.Short && !settings.AllowShort)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "short-disabled",
                "Short entries are disabled by hard risk settings.");
        }

        decimal spread = state.Ask - state.Bid;
        if (spread > settings.MaxSpreadPrice)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "spread-price-limit",
                "Current spread exceeds the configured price limit.");
        }

        if (!TryFeature(state, SpreadAtrRatioFeature, out double spreadAtrRatio))
        {
            return Reject(
                state,
                modelDecision,
                now,
                "spread-atr-unavailable",
                "Spread/ATR ratio is unavailable.");
        }

        if (spreadAtrRatio > settings.MaxSpreadAtrRatio)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "spread-atr-limit",
                "Current spread/ATR ratio exceeds the configured limit.");
        }

        bool newsBlockingEnabled =
            settings.HighImpactNewsBlockBeforeSec > 0
            || settings.HighImpactNewsBlockAfterSec > 0;

        if (newsBlockingEnabled)
        {
            if (!state.Readiness.NewsDataAvailable
                || !TryFeature(state, HighImpactNewsFeature, out double highImpact))
            {
                return Reject(
                    state,
                    modelDecision,
                    now,
                    "news-unavailable",
                    "News blocking is enabled but causal news data is unavailable.");
            }

            if (highImpact >= 0.5)
            {
                return Reject(
                    state,
                    modelDecision,
                    now,
                    "high-impact-news",
                    "Entry falls inside the configured high-impact news window.");
            }
        }

        if (portfolio.Equity <= 0 || portfolio.FreeMargin < 0)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "invalid-account-state",
                "Equity/free margin are invalid.");
        }

        double currentFreeMarginPct =
            decimal.ToDouble(portfolio.FreeMargin / portfolio.Equity * 100m);
        if (currentFreeMarginPct < settings.MinFreeMarginPct)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "free-margin-limit",
                "Current free-margin percentage is below the configured minimum.");
        }

        return null;
    }

    private RiskDecision? ValidateProfileAndLedger(
        XauMarketState state,
        XauDecision modelDecision,
        PortfolioState portfolio,
        RiskSettings settings,
        RiskSymbolProfile profile,
        OwnedRiskLedgerSnapshot ledger,
        DateTimeOffset now)
    {
        if (!string.Equals(profile.Symbol, state.Symbol, StringComparison.Ordinal)
            || !string.Equals(
                profile.BrokerSymbol,
                state.BrokerSymbol,
                StringComparison.Ordinal))
        {
            return Reject(
                state,
                modelDecision,
                now,
                "symbol-profile-mismatch",
                "Risk symbol profile does not match the market state.");
        }

        if (profile.Point <= 0
            || profile.TickSize <= 0
            || profile.TickValue <= 0
            || profile.MinVolume <= 0
            || profile.MaxVolume < profile.MinVolume
            || profile.VolumeStep <= 0
            || profile.MinStopDistance < 0
            || profile.EstimatedMarginPerLotMoney <= 0)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "invalid-symbol-profile",
                "Broker symbol/risk metadata is invalid.");
        }

        if (!ledger.IsReady || ledger.DayStartEquity <= 0)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "risk-ledger-not-ready",
                "Owned daily risk ledger is not reconstructed for the trading day.");
        }

        decimal dailyLoss = Math.Max(0, -ledger.RealizedNetPnlMoney);
        if (settings.MaxDailyLossMoney is decimal maxMoney
            && dailyLoss >= maxMoney)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "daily-loss-money-lock",
                "Owned realized daily loss reached the configured money limit.");
        }

        decimal dailyLossPct = dailyLoss / ledger.DayStartEquity * 100m;
        if (dailyLossPct >= (decimal)settings.MaxDailyLossPct)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "daily-loss-pct-lock",
                "Owned realized daily loss reached the configured percentage limit.");
        }

        if (ledger.ClosedTrades >= settings.MaxTradesPerDay)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "daily-trade-limit",
                "Owned closed-trade count reached the configured daily limit.");
        }

        if (ledger.LastLossAtUtc is DateTimeOffset lossAt
            && now - lossAt < TimeSpan.FromSeconds(settings.CooldownAfterLossSec))
        {
            return Reject(
                state,
                modelDecision,
                now,
                "loss-cooldown",
                "Loss cooldown is still active.");
        }

        if (ledger.LastExecutionFailureAtUtc is DateTimeOffset failureAt
            && now - failureAt
                < TimeSpan.FromSeconds(settings.CooldownAfterExecutionFailureSec))
        {
            return Reject(
                state,
                modelDecision,
                now,
                "execution-failure-cooldown",
                "Execution-failure cooldown is still active.");
        }

        int ownedPositions = portfolio.Positions.Count(IsOwnedPosition);
        if (ownedPositions >= settings.MaxConcurrentPositions)
        {
            return Reject(
                state,
                modelDecision,
                now,
                "concurrent-position-limit",
                "Owned concurrent-position limit has been reached.");
        }

        return null;
    }

    private bool IsOwnedPosition(PositionState position)
    {
        return position.Ownership.MagicNumber == _policy.Ownership.MagicNumber
            && string.Equals(
                position.Ownership.StrategyId,
                _policy.Ownership.StrategyId,
                StringComparison.Ordinal);
    }

    private RiskDecision Reject(
        XauMarketState state,
        XauDecision decision,
        DateTimeOffset now,
        string reasonCode,
        string reason)
    {
        return new RiskDecision(
            ContractVersions.RiskDecisionV1,
            DeterministicRiskDecisionId(
                state.MarketStateId,
                decision.DecisionId,
                _policy.RiskPolicyVersion),
            state.MarketStateId,
            decision.DecisionId,
            RiskDecisionOutcome.Rejected,
            reasonCode,
            reason,
            authorizedVolumeLots: null,
            authorizedRiskMoney: null,
            protectiveStopPrice: null,
            _policy.RiskPolicyVersion,
            now);
    }

    private static bool TryFeature(
        XauMarketState state,
        string name,
        out double value)
    {
        NumericFeatureValue? feature = state.Features.FirstOrDefault(
            feature => string.Equals(feature.Name, name, StringComparison.Ordinal));

        if (feature?.IsAvailable == true
            && feature.Value is double numeric
            && double.IsFinite(numeric))
        {
            value = numeric;
            return true;
        }

        value = default;
        return false;
    }

    private static decimal FloorToStep(decimal value, decimal step)
    {
        return Math.Floor(value / step) * step;
    }

    private static Guid DeterministicRiskDecisionId(
        Guid marketStateId,
        Guid decisionId,
        string policyVersion)
    {
        string identity = string.Join(
            "|",
            "risk-v1",
            marketStateId.ToString("D"),
            decisionId.ToString("D"),
            policyVersion);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16));
    }
}
