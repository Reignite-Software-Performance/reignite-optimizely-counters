using EPiServer.Commerce.Order;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;
#if CMS13
// V13 moved the Intercept extension out of Microsoft.Extensions.DependencyInjection into
// EPiServer.DependencyInjection.ServiceCollectionExtensions.
using EPiServer.DependencyInjection;
#endif
using Microsoft.Extensions.Logging;
#if !CMS11
using Microsoft.Extensions.DependencyInjection;
#endif
using Reignite.Optimizely.Counters.Commerce.Decorators;
using Reignite.Optimizely.Counters.Core.Configuration;
using Reignite.Optimizely.Counters.Core.Deployment;
using Reignite.Optimizely.Counters.Core.Diagnostics;
using Reignite.Optimizely.Counters.Core.Http;
using Reignite.Optimizely.Counters.Core.Telemetry;
using Reignite.Optimizely.Counters.Shared;
using Reignite.Optimizely.Counters.VersionDetection;
using static Reignite.Optimizely.Counters.Shared.ModuleSupport;

namespace Reignite.Optimizely.Counters.Commerce.Initialization
{
    /// <summary>
    /// Initialization module for Optimizely Commerce Performance Counters.
    /// Wraps the Commerce services that carry the counters in instrumented decorators.
    /// </summary>
    // No [ModuleDependency]: all the work happens in ConfigureContainer, which the framework
    // runs while building the container - before any module initializes.
    [InitializableModule]
    public class CommercePerformanceCountersModule : IConfigurableModule
    {
        private ILogger<CommercePerformanceCountersModule>? _logger;

        // Held separately from _logger so Initialize knows what to flush. ConfigureContainer runs
        // before the container can be resolved from, so everything it logs is buffered here and
        // replayed through the host's own logger once one exists.
        private DeferredLogger<CommercePerformanceCountersModule>? _deferredLogger;

        // Read in ConfigureContainer and used again in Initialize. Defaulted rather than nullable
        // so that a module whose ConfigureContainer somehow did not run still behaves.
        private InstrumentationOptions _options = new InstrumentationOptions();

        /// <summary>
        /// Registers the metric tracker and wraps the instrumented Commerce services. Runs while
        /// the container is being built, before any module initializes.
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        /// <remarks>
        /// Deferred to <see cref="ServiceConfigurationContext.ConfigurationComplete"/> on CMS 12 and
        /// 13 for the same two reasons as the CMS module: the Commerce and CMS core services are not
        /// registered yet during ConfigureContainer on CMS 13, so <c>Intercept&lt;T&gt;</c> throws and
        /// takes the site down; and the host's <c>IConfiguration</c> is not findable yet, so every
        /// setting including the master switch silently fell back to its default. V11 keeps
        /// registering here - it configures through <c>IServiceConfigurationProvider</c> and reads
        /// <c>appSettings</c>, neither of which has the ordering problem.
        /// </remarks>
        public void ConfigureContainer(ServiceConfigurationContext context)
        {
            _logger = _deferredLogger = ResolveLogger<CommercePerformanceCountersModule>(context);

#if CMS11
            Configure(context);
#else
            // The captured context, not the event argument: the event exposes
            // ServiceConfigurationEventArgs, while every helper here takes the
            // ServiceConfigurationContext (and its IServiceCollection) that the framework
            // handed to ConfigureContainer. Both point at the same collection.
            context.ConfigurationComplete += (_, _) => Configure(context);
#endif
        }

#if !CMS11
        /// <summary>
        /// Test seam: runs the pass <see cref="ConfigureContainer"/> defers to
        /// <see cref="ServiceConfigurationContext.ConfigurationComplete"/>.
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        /// <remarks>
        /// The framework raises that event itself and nothing outside it can, so without this the
        /// registrations could not be exercised at all and a broken intercept would go unnoticed
        /// until a site started. Internal: it is not part of the package's surface and no site
        /// should call it. Production goes through the event.
        /// </remarks>
        internal void CompleteConfiguration(ServiceConfigurationContext context) => Configure(context);
#endif

        /// <summary>
        /// The actual configuration pass: reads the settings, validates the version and registers
        /// the tracker, the decorators and the inert-until-Initialize providers.
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        private void Configure(ServiceConfigurationContext context)
        {
            _options = LoadOptions(context, _logger);

            if (!_options.Enabled)
            {
                // One switch covers both packages: they read the same section, so a site that turns
                // instrumentation off does not have to know which of the two it has installed.
                _logger?.LogInformation(
                    "Optimizely Commerce Performance Counters are switched off by configuration " +
                    "('{SectionName}:Enabled' is false). Nothing is decorated and no probe runs.",
                    InstrumentationOptions.SectionName);
                return;
            }

            _logger?.LogInformation(
                "Commerce Version Detection:\n{VersionInfo}",
                OptimizelyVersionDetector.GetDetailedVersionInfo());

            VersionCompatibility.Validate(
                OptimizelyVersionDetector.DetectVersion(),
                OptimizelyVersionDetector.GetExpectedVersion(),
                packageName: "Commerce",
                requiredAssembly: "EPiServer.Commerce.Core",
                _logger);

            ConfigureTelemetry(context, _logger, _options.Meter);

            // Registered before the log line, not inside it. A null-conditional call evaluates
            // nothing to its right, so `_logger?.LogInformation(..., RegisterMetricTracker(...))`
            // silently skips the registration on any host where the logger could not be resolved.
            var tracker = RegisterMetricTracker(context, _options.Meter);
            _logger?.LogInformation("Registered IMetricTracker: {Tracker}", tracker);

            RegisterDecorators(context);

#if !CMS11
            if (_options.Logging.Enabled)
            {
                // Registered here, not started here. The logging factory reads its providers out of
                // the container once, so the provider has to exist before the container is built;
                // it stays inert until Initialize publishes a recorder for it to count into.
                // TryAddEnumerable makes this safe next to the CMS package doing the same.
                RegisterLogWriteRateProvider(context);
            }

            if (_options.Http.Enabled)
            {
                // Same shape again: the host reads its startup filters while building the request
                // pipeline, so the middleware has to be registered before that and goes in inert.
                // TryAddEnumerable inside the registration keeps this safe next to the CMS package
                // doing the same, which matters more here than for the logging provider - two
                // middlewares would classify every response twice.
                RegisterHttpCacheability(context, _logger);
            }
#endif

            _logger?.LogInformation("Optimizely Commerce Performance Counters configured successfully");
        }

        /// <summary>
        /// Replays what <see cref="ConfigureContainer"/> logged, now that a container exists to
        /// resolve the host's own logger from, and starts the runtime probes. No registration
        /// happens here.
        /// </summary>
        /// <param name="context">Initialization context supplied by the framework.</param>
        /// <remarks>
        /// The probes measure the process rather than Commerce, so a Commerce-only host wants them
        /// as much as a CMS one does. On a host running both packages whichever module initializes
        /// first starts them and the other's call is a no-op.
        /// </remarks>
        public void Initialize(InitializationEngine context)
        {
            _logger = FlushLogger(_deferredLogger, context) ?? _logger;
            _deferredLogger = null;

            if (!_options.Enabled)
            {
                // ConfigureContainer already said why, and it said it before the container was
                // built, so this is where that line reaches the host's log.
                return;
            }

            StartRuntimeProbes(context, _options.Probes, _logger);
            StartLogWriteRate(context, _options.Logging, _logger);
            StartHttpCacheability(context, _options.Http, _logger);
            StartDeploymentTracking(context, _options.Deployment, _logger);

            _logger?.LogInformation("Optimizely Commerce Performance Counters initialized");
        }

        /// <summary>
        /// Stops the runtime probes. The decorators need no teardown - they are owned by the
        /// container and go with it - but the probes own threads, so they do.
        /// </summary>
        /// <param name="context">Initialization context supplied by the framework.</param>
        public void Uninitialize(InitializationEngine context)
        {
            RuntimeProbes.Stop();
            LogWriteRateMonitor.Stop();
            HttpCacheabilityMonitor.Stop();
            DeploymentTracker.Stop();

            _logger?.LogInformation("Optimizely Commerce Performance Counters uninitialized");
        }

        private void RegisterDecorators(ServiceConfigurationContext context)
        {
            _logger?.LogInformation("Registering Commerce performance counter decorators");

            // The lambda's first argument is IServiceLocator on V11 and IServiceProvider on
            // V12/V13, so it is left implicitly typed and every resolution goes through Resolve.
            context.Services.Intercept<IOrderRepository>(
                (services, defaultImplementation) => new InstrumentedOrderRepository(
                    defaultImplementation,
                    Resolve<IMetricTracker>(services),
                    Resolve<ILogger<InstrumentedOrderRepository>>(services)));

            _logger?.LogInformation("Registered decorators: IOrderRepository");

            // TODO: Add more decorators
            // - IPriceService / IPriceDetailService (pricing lookups)
            // - IInventoryService (stock checks)
            // - IPromotionEngine (promotion evaluation)
            // - IPaymentProcessor (payment gateway round trips)
        }
    }
}
