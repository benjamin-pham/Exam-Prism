namespace ExamPrism.Infrastructure.Telemetry;

using System;
using System.Diagnostics;
using OpenTelemetry.Trace;

public sealed class AdaptiveTraceSampler : Sampler
{
    private readonly Sampler _ratioSampler;
    private readonly double _ratio;

    public AdaptiveTraceSampler(double ratio = 1.0)
    {
        _ratio = Math.Clamp(ratio, 0.0, 1.0);
        _ratioSampler = new TraceIdRatioBasedSampler(_ratio);
        Description = $"AdaptiveTraceSampler{{ratio={_ratio:F2}}}";
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        // 1. Parent is recorded -> always sample downstream spans
        if (samplingParameters.ParentContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded))
        {
            return new SamplingResult(SamplingDecision.RecordAndSample);
        }

        // 2. Activity name contains "exam.critical" or "error"
        if (!string.IsNullOrEmpty(samplingParameters.Name) &&
            (samplingParameters.Name.Contains("exam.critical", StringComparison.OrdinalIgnoreCase) ||
             samplingParameters.Name.Contains("error", StringComparison.OrdinalIgnoreCase)))
        {
            return new SamplingResult(SamplingDecision.RecordAndSample);
        }

        // 3. Activity tags contain "exam.critical" or "error"
        if (samplingParameters.Tags != null)
        {
            foreach (var tag in samplingParameters.Tags)
            {
                if (tag.Key.Contains("exam.critical", StringComparison.OrdinalIgnoreCase) ||
                    tag.Key.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    return new SamplingResult(SamplingDecision.RecordAndSample);
                }

                if (tag.Value is string strVal &&
                    (strVal.Contains("exam.critical", StringComparison.OrdinalIgnoreCase) ||
                     strVal.Contains("error", StringComparison.OrdinalIgnoreCase)))
                {
                    return new SamplingResult(SamplingDecision.RecordAndSample);
                }

                if (tag.Value is bool bVal && bVal && tag.Key.Contains("critical", StringComparison.OrdinalIgnoreCase))
                {
                    return new SamplingResult(SamplingDecision.RecordAndSample);
                }
            }
        }

        // 4. Fallback to ratio-based sampling
        return _ratioSampler.ShouldSample(samplingParameters);
    }
}
