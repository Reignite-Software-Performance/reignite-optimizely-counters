using System.Linq;
using System.Threading;
using Reignite.Optimizely.Counters.Core.Diagnostics;
using Reignite.Optimizely.Counters.Tests.Infrastructure;
using Xunit;
using Names = Reignite.Optimizely.Counters.Core.Telemetry.CounterNames.Runtime.Process;

namespace Reignite.Optimizely.Counters.Tests.Diagnostics
{
    /// <summary>
    /// Covers the process uptime counter, which is a gauge in a package where nearly everything
    /// else is a rate - and the two have opposite flush semantics.
    /// </summary>
    public class ProcessUptimeTests
    {
        [Fact]
        public void The_flush_reports_the_uptime()
        {
            var tracker = new RecordingMetricTracker();
            using var reporter = new ProcessUptimeReporter(tracker);

            MetricFlush.Run(reporter);

            var reported = tracker.Metrics.Single();

            Assert.Equal(Names.UptimeSeconds, reported.Name);
            Assert.True(reported.Value >= 0.0, $"Uptime read as {reported.Value}.");
        }

        [Fact]
        public void The_uptime_is_the_process_age_rather_than_the_reporter_age()
        {
            var tracker = new RecordingMetricTracker();

            // Measured rather than assumed. The earlier form of this test asserted the process was
            // more than half a second old, on the reasoning that assembly load and discovery take
            // longer than that - which is true on most machines and false on a fast one, where the
            // host can be under 400ms old when the test body runs and the assertion fails against
            // correct code. Comparing the reported value with the process's own start time tests
            // the same property without depending on how quickly the host got here.
            var expectedStart = ProcessStartUtc();

            using var reporter = new ProcessUptimeReporter(tracker);

            MetricFlush.Run(reporter);

            var reported = ValueOf(tracker);

            if (expectedStart is null)
            {
                // The host refused to give up its start time, which is the documented case where
                // the reporter falls back to timing from instrumentation start. There is nothing
                // to compare against, so assert the fallback is at least plausible rather than
                // fail a correct implementation for a host limitation.
                Assert.True(reported >= 0.0, $"Uptime read as {reported} seconds.");
                return;
            }

            var expected = (System.DateTime.UtcNow - expectedStart.Value).TotalSeconds;

            Assert.True(
                System.Math.Abs(reported - expected) < 2.0,
                $"Uptime read as {reported} seconds while the process is {expected} seconds old, " +
                "which is the reporter's own age rather than the process's.");
        }

        [Fact]
        public void The_uptime_climbs_across_flushes_instead_of_resetting()
        {
            var tracker = new RecordingMetricTracker();
            using var reporter = new ProcessUptimeReporter(tracker);

            MetricFlush.Run(reporter);
            var first = ValueOf(tracker);

            Thread.Sleep(50);

            MetricFlush.Run(reporter);
            var second = ValueOf(tracker);

            // A gauge, not a rate. Every other reporter in this package exchanges its accumulators
            // to zero on flush, and doing that here would turn the counter into a sawtooth with a
            // sixty second period - which is what it is supposed to distinguish a restart from.
            Assert.True(second > first, $"Second reading {second} did not exceed the first {first}.");
        }

        [Fact]
        public void A_reporter_will_not_take_a_null_tracker()
        {
            // Fails where the probes do, at construction in the initialization module, rather
            // than once a minute on a timer thread that has nowhere to report it.
            Assert.Throws<System.ArgumentNullException>(() => new ProcessUptimeReporter(null!));
        }

        private static double ValueOf(RecordingMetricTracker tracker) =>
            tracker.Metrics.Last(m => m.Name == Names.UptimeSeconds).Value;

        /// <summary>
        /// The process's start time as the production code resolves it, or null where the host
        /// refuses to part with one. Mirrors <c>ResolveProcessStartUtc</c> deliberately: the test
        /// and the reporter have to agree on what "the process age" means for the comparison to
        /// be meaningful.
        /// </summary>
        private static System.DateTime? ProcessStartUtc()
        {
            try
            {
                using var current = System.Diagnostics.Process.GetCurrentProcess();
                var started = current.StartTime.ToUniversalTime();

                return started > System.DateTime.UtcNow || started < System.DateTime.UtcNow.AddYears(-1)
                    ? (System.DateTime?)null
                    : started;
            }
            catch
            {
                return null;
            }
        }
    }
}
