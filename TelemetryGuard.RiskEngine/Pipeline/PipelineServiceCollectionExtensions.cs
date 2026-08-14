using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.ML;
using TelemetryGuard.RiskEngine.Contracts;
using TelemetryGuard.RiskEngine.Enrichment;
using TelemetryGuard.RiskEngine.Features;
using TelemetryGuard.RiskEngine.Rules;
using TelemetryGuard.RiskEngine.Scoring;
using TelemetryGuard.RiskEngine.Velocity;

namespace TelemetryGuard.RiskEngine.Pipeline;

public static class PipelineServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full scoring pipeline (RSK-07): session-state reader, whitelist
    /// check (+ TryAdd no-op cache rebuilder — the API host overrides it with the DAT-07
    /// adapter), cached enrichment decoration, campaign provider (TryAdd null impl — the
    /// API host wires the DAT-05-backed one), and IScoringPipeline itself. Also applies
    /// the earlier AddIpEnrichment/AddVelocityStore/AddFeatureExtraction/AddT1Rules/
    /// AddHeuristicScorer registrations when the host has not already, guarded so
    /// double-registration is harmless and pre-registered fakes win (testability).
    ///
    /// RSK-08 (D18): also binds <see cref="ScoringOptions"/> ("Scoring" section — does
    /// NOT relocate the pre-existing "Scoring:Bands"/"Scoring:Heuristic" sub-sections)
    /// and, when a model is configured, loads a pooled LightGBM scorer. See
    /// <see cref="ScoringOptions"/>'s XML doc for the two rollout config snippets.
    /// </summary>
    public static IServiceCollection AddScoringPipeline(this IServiceCollection services, IConfiguration config)
    {
        // Enrichment: keep RSK-02's AddIpEnrichment, then override the interface
        // registration so the pipeline receives the memory-cached decorator over the
        // concrete service. A pre-registered fake IIpEnrichmentService (without the
        // concrete) is left untouched.
        if (!IsRegistered(services, typeof(IIpEnrichmentService))
            && !IsRegistered(services, typeof(IpEnrichmentService)))
        {
            services.AddIpEnrichment(config);
        }

        if (IsRegistered(services, typeof(IpEnrichmentService)))
        {
            services.Replace(ServiceDescriptor.Singleton<IIpEnrichmentService>(
                sp => new CachedIpEnrichmentService(sp.GetRequiredService<IpEnrichmentService>())));
        }

        if (!IsRegistered(services, typeof(IVelocityStore)))
        {
            services.AddVelocityStore(config);
        }

        if (!IsRegistered(services, typeof(IFeatureExtractor)))
        {
            services.AddFeatureExtraction(config);
        }

        if (!IsRegistered(services, typeof(IT1RuleEngine)))
        {
            services.AddT1Rules(config);
        }

        if (!IsRegistered(services, typeof(IScorer)))
        {
            services.AddHeuristicScorer(config);
        }

        // RSK-08 (D18): heuristic-to-model swap is a config change only. A ModelPath
        // loads the pooled LightGBM scorer regardless of Scorer/Mode (it may end up
        // enforcing, shadowing, or — for an unusual Enforce+Heuristic+ModelPath
        // combination — simply sitting unused); Scorer/Mode decide what happens with
        // it. See ScoringOptions' XML doc for the two rollout snippets.
        services.Configure<ScoringOptions>(config.GetSection(ScoringOptions.SectionName));
        var scoringOptions = config.GetSection(ScoringOptions.SectionName).Get<ScoringOptions>() ?? new ScoringOptions();
        if (!string.IsNullOrWhiteSpace(scoringOptions.ModelPath))
        {
            var modelPath = scoringOptions.ModelPath;
            var modelFile = Path.Combine(modelPath, "model.zip");
            var scorerVersion = ModelMetadataReader.ReadScorerVersion(modelPath);

            services.AddPredictionEnginePool<MlFeatureRow, MlPrediction>()
                .FromFile(filePath: modelFile, watchForChanges: true);
            services.TryAddSingleton<MlNetScorer>(sp => new MlNetScorer(
                sp.GetRequiredService<PredictionEnginePool<MlFeatureRow, MlPrediction>>(), scorerVersion));

            if (scoringOptions.Mode == ScoringMode.Enforce && scoringOptions.Scorer == "MlNet")
            {
                // Promotion (human decision, D18): the model enforces directly,
                // replacing whatever IScorer the block above registered (heuristic by
                // default, or a pre-registered test fake).
                services.Replace(ServiceDescriptor.Singleton<IScorer>(
                    sp => sp.GetRequiredService<MlNetScorer>()));
            }
            else if (scoringOptions.Mode == ScoringMode.ListenOnly)
            {
                // Listen-only (D18): the enforcing IScorer is left untouched (heuristic
                // enforces regardless of Scorer); the model runs only as a
                // non-enforcing shadow scorer, logged and never consulted for the verdict.
                services.TryAddSingleton<IShadowScorer>(
                    sp => new ShadowScorerAdapter(sp.GetRequiredService<MlNetScorer>()));
            }
        }
        else if (scoringOptions.Scorer == "MlNet")
        {
            throw new InvalidOperationException(
                "Scoring:Scorer is \"MlNet\" but Scoring:ModelPath is not set (RSK-08) — cannot load a model.");
        }

        services.TryAddScoped<ISessionStateStore, RedisSessionStateStore>();
        services.TryAddSingleton<IWhitelistCacheRebuilder, NoOpWhitelistCacheRebuilder>();
        services.TryAddScoped<IWhitelistCheck, RedisWhitelistCheck>();
        services.TryAddSingleton<ICampaignContextProvider, NullCampaignContextProvider>();
        services.TryAddScoped<IScoringPipeline, ScoringPipeline>();
        return services;
    }

    private static bool IsRegistered(IServiceCollection services, Type serviceType)
        => services.Any(d => d.ServiceType == serviceType);
}
