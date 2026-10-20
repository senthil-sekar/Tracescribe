using System.Diagnostics;
using Tracescribe.Digest;

namespace Tracescribe.Tests;

/// <summary>Gives each test its own ActivitySource and collector so parallel tests don't interfere.</summary>
internal sealed class TestTrace : IDisposable
{
    public TestTrace(TracescribeEnricher? enricher = null)
    {
        Source = new ActivitySource("Tracescribe.Tests." + Guid.NewGuid().ToString("N"));
        Collector = new TraceCollector(s => s.Name == Source.Name, enricher);
    }

    public ActivitySource Source { get; }

    public TraceCollector Collector { get; }

    public SpanRecord Span(string name) => Collector.Spans.Single(s => s.Name == name);

    public void Dispose()
    {
        Collector.Dispose();
        Source.Dispose();
    }
}
