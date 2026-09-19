using XauScalp.Domain;

namespace XauScalp.Features;

public interface IXauFeatureEngine
{
    string FeatureSchemaVersion { get; }

    string FeatureEngineVersion { get; }

    XauMarketState Update(MarketEvent marketEvent);

    void ObserveContext(MarketEvent marketEvent);

    void SetExternalContext(FeatureExternalContext? context);
}
