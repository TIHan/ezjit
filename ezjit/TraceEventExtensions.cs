using Microsoft.Diagnostics.Tracing;

namespace EzJit
{
    static class TraceEventExtensions
    {
        public static string FullEventName(this TraceEvent traceEvent)
        {
            return $"{traceEvent.ProviderName}/{traceEvent.EventName}";
        }
    }
}