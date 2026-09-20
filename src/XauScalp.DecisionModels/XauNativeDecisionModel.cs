using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using XauScalp.Domain;

namespace XauScalp.DecisionModels;

public sealed class NativeModelInputException : InvalidDataException
{
    public NativeModelInputException(string message)
        : base(message)
    {
    }
}

public sealed class XauNativeDecisionModel : IXauDecisionModel
{
    private readonly LoadedNativeArtifact _loaded;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyDictionary<NativeHeadKind, NativeOutputHead> _heads;

    public XauNativeDecisionModel(
        LoadedNativeArtifact loaded,
        TimeProvider? timeProvider = null)
    {
        _loaded = loaded ?? throw new ArgumentNullException(nameof(loaded));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _heads = loaded.Artifact.Heads.ToDictionary(static head => head.Name);
    }

    public string ModelId => _loaded.Artifact.ModelId;

    public string ModelVersion => _loaded.Artifact.ModelVersion;

    public string ArtifactSha256 => _loaded.ArtifactSha256;

    public Task<XauDecision> EvaluateAsync(
        XauMarketState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        long started = Stopwatch.GetTimestamp();
        double[] normalized = BuildNormalizedVector(state);

        double pUp5 = Probability(NativeHeadKind.Up5First, normalized);
        double pDown5 = Probability(NativeHeadKind.Down5First, normalized);
        double pUp10 = Probability(NativeHeadKind.Up10First, normalized);
        double pDown10 = Probability(NativeHeadKind.Down10First, normalized);
        double pLongAdverse = Probability(
            NativeHeadKind.LongAdverseFirst,
            normalized);
        double pShortAdverse = Probability(
            NativeHeadKind.ShortAdverseFirst,
            normalized);
        double pContinuation = Probability(
            NativeHeadKind.Continuation,
            normalized);
        double pReversal = Probability(
            NativeHeadKind.Reversal,
            normalized);
        double pFalseBreak = Probability(
            NativeHeadKind.FalseBreak,
            normalized);

        double longEdge = pUp5 - pLongAdverse;
        double shortEdge = pDown5 - pShortAdverse;
        double minimumEdge = _loaded.Artifact.ActionPolicy.MinimumEdge;

        TradeAction action;
        if (longEdge >= minimumEdge && longEdge >= shortEdge)
        {
            action = TradeAction.Long;
        }
        else if (shortEdge >= minimumEdge)
        {
            action = TradeAction.Short;
        }
        else
        {
            action = TradeAction.Wait;
        }

        double actionProbability = action switch
        {
            TradeAction.Long => pUp5,
            TradeAction.Short => pDown5,
            _ => ClampProbability(1 - Math.Max(pUp5, pDown5)),
        };

        double pAdverse = action switch
        {
            TradeAction.Long => pLongAdverse,
            TradeAction.Short => pShortAdverse,
            _ => Math.Max(pLongAdverse, pShortAdverse),
        };

        double confidence = ClampProbability(
            2 * Math.Max(
                Math.Abs(pUp5 - 0.5),
                Math.Abs(pDown5 - 0.5)));

        var decision = new XauDecision(
            ContractVersions.DecisionV1,
            DeterministicDecisionId(
                _loaded.ArtifactSha256,
                state.MarketStateId),
            state.MarketStateId,
            DecisionModelType.XauNative,
            action,
            actionProbability,
            pUp5,
            pDown5,
            pUp10,
            pDown10,
            pAdverse,
            pContinuation,
            pReversal,
            pFalseBreak,
            confidence,
            pHold: null,
            pExitNow: null,
            pTp5FromHere: null,
            pTp10FromHere: null,
            ModelId,
            ModelVersion,
            state.FeatureSchemaVersion,
            _timeProvider.GetUtcNow(),
            Stopwatch.GetElapsedTime(started));

        return Task.FromResult(decision);
    }

    private double[] BuildNormalizedVector(XauMarketState state)
    {
        if (!string.Equals(
            state.FeatureSchemaVersion,
            _loaded.Artifact.FeatureSchemaVersion,
            StringComparison.Ordinal))
        {
            throw new NativeModelInputException(
                $"Feature schema mismatch. Model expects {_loaded.Artifact.FeatureSchemaVersion}, state has {state.FeatureSchemaVersion}.");
        }

        if (!state.Readiness.RequiredP0Ready)
        {
            throw new NativeModelInputException(
                "XAU Native inference fails closed while required P0 data is not ready.");
        }

        var byName = state.Features.ToDictionary(
            static feature => feature.Name,
            StringComparer.Ordinal);

        double[] normalized =
            new double[_loaded.Artifact.FeatureNames.Length];

        for (int index = 0;
            index < _loaded.Artifact.FeatureNames.Length;
            index++)
        {
            string featureName = _loaded.Artifact.FeatureNames[index];
            if (!byName.TryGetValue(
                    featureName,
                    out NumericFeatureValue? feature)
                || !feature.IsAvailable
                || feature.Value is not double value
                || !double.IsFinite(value))
            {
                throw new NativeModelInputException(
                    $"Required native-model feature {featureName} is missing or unavailable.");
            }

            NativeFeatureNormalization normalization =
                _loaded.Artifact.Normalization[index];

            double standardized =
                (value - normalization.Mean) / normalization.StdDev;

            if (!double.IsFinite(standardized))
            {
                throw new NativeModelInputException(
                    $"Normalized feature {featureName} is not finite.");
            }

            normalized[index] = standardized;
        }

        return normalized;
    }

    private double Probability(
        NativeHeadKind headKind,
        double[] normalized)
    {
        NativeOutputHead head = _heads[headKind];

        if (head.Mode == NativeHeadMode.Constant)
        {
            return head.ConstantProbability!.Value;
        }

        double raw = head.Intercept;
        for (int index = 0; index < normalized.Length; index++)
        {
            raw += head.Weights[index] * normalized[index];
        }

        double calibratedLogit =
            (head.CalibrationA * raw) + head.CalibrationB;

        return Sigmoid(calibratedLogit);
    }

    private static double Sigmoid(double value)
    {
        double clamped = Math.Clamp(value, -40, 40);
        return 1 / (1 + Math.Exp(-clamped));
    }

    private static double ClampProbability(double value)
    {
        return Math.Clamp(value, 0, 1);
    }

    private static Guid DeterministicDecisionId(
        string artifactSha256,
        Guid marketStateId)
    {
        byte[] bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{artifactSha256}|{marketStateId:D}"));

        return new Guid(bytes.AsSpan(0, 16));
    }
}
