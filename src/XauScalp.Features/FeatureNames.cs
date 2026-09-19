namespace XauScalp.Features;

public static class FeatureNames
{
    public const string SpreadPrice = nameof(SpreadPrice);
    public const string SpreadPoints = nameof(SpreadPoints);
    public const string SpreadAtrRatio = nameof(SpreadAtrRatio);
    public const string TickSize = nameof(TickSize);
    public const string TickValue = nameof(TickValue);
    public const string MinStopDistance = nameof(MinStopDistance);
    public const string EstimatedLatencyMs = nameof(EstimatedLatencyMs);
    public const string EstimatedSlippagePoints = nameof(EstimatedSlippagePoints);
    public const string SessionCode = nameof(SessionCode);
    public const string MinuteOfDay = nameof(MinuteOfDay);
    public const string DayOfWeek = nameof(DayOfWeek);
    public const string NewsDistanceBeforeSec = nameof(NewsDistanceBeforeSec);
    public const string NewsDistanceAfterSec = nameof(NewsDistanceAfterSec);
    public const string IsHighImpactNewsWindow = nameof(IsHighImpactNewsWindow);

    public const string M1BarAgeMs = nameof(M1BarAgeMs);
    public const string M1BarProgressPct = nameof(M1BarProgressPct);
    public const string M1Open = nameof(M1Open);
    public const string M1LiveHigh = nameof(M1LiveHigh);
    public const string M1LiveLow = nameof(M1LiveLow);
    public const string M1LivePrice = nameof(M1LivePrice);
    public const string M1LiveRangePrice = nameof(M1LiveRangePrice);
    public const string M1LiveBodyPrice = nameof(M1LiveBodyPrice);
    public const string M1LiveBodyRatio = nameof(M1LiveBodyRatio);
    public const string M1UpperWickRatio = nameof(M1UpperWickRatio);
    public const string M1LowerWickRatio = nameof(M1LowerWickRatio);
    public const string M1DistanceFromOpen = nameof(M1DistanceFromOpen);
    public const string M1DistanceFromHigh = nameof(M1DistanceFromHigh);
    public const string M1DistanceFromLow = nameof(M1DistanceFromLow);
    public const string M1RangeAtrRatio = nameof(M1RangeAtrRatio);

    public const string Return250ms = nameof(Return250ms);
    public const string Return500ms = nameof(Return500ms);
    public const string Return1s = nameof(Return1s);
    public const string Return2s = nameof(Return2s);
    public const string Return3s = nameof(Return3s);
    public const string Return5s = nameof(Return5s);
    public const string Return10s = nameof(Return10s);

    public const string Velocity500ms = nameof(Velocity500ms);
    public const string Velocity1s = nameof(Velocity1s);
    public const string Velocity3s = nameof(Velocity3s);
    public const string Velocity5s = nameof(Velocity5s);

    public const string Acceleration1s = nameof(Acceleration1s);
    public const string Acceleration3s = nameof(Acceleration3s);
    public const string PeakVelocityUp = nameof(PeakVelocityUp);
    public const string PeakVelocityDown = nameof(PeakVelocityDown);
    public const string DecelerationRatio = nameof(DecelerationRatio);
    public const string DirectionFlipAgeMs = nameof(DirectionFlipAgeMs);

    public const string TickCount250ms = nameof(TickCount250ms);
    public const string TickCount1s = nameof(TickCount1s);
    public const string TickCount5s = nameof(TickCount5s);
    public const string TickRate250ms = nameof(TickRate250ms);
    public const string TickRate1s = nameof(TickRate1s);
    public const string TickRate5s = nameof(TickRate5s);
    public const string MeanTickIntervalMs = nameof(MeanTickIntervalMs);
    public const string TickIntervalStdMs = nameof(TickIntervalStdMs);
    public const string UpTickRatio = nameof(UpTickRatio);
    public const string DownTickRatio = nameof(DownTickRatio);
    public const string MicroRange1s = nameof(MicroRange1s);
    public const string MicroRange5s = nameof(MicroRange5s);
    public const string BurstZScore = nameof(BurstZScore);
}
