using System;

namespace Autoclicker.Core;

/// <summary>
/// Generates delay intervals (in milliseconds) between successive click actions.
/// Supports fixed intervals (when Min == Max), uniform random ranges, and an internal
/// truncated Gaussian distribution centered in the [Min, Max] range.
/// </summary>
public sealed class IntervalProvider
{
    private readonly int _minMs;
    private readonly int _maxMs;
    private readonly IntervalDistributionKind _distribution;
    private readonly Random _rng;

    public IntervalProvider(
        int minMs,
        int maxMs,
        IntervalDistributionKind distribution = IntervalDistributionKind.Uniform,
        Random? rng = null)
    {
        _minMs = Math.Max(1, Math.Min(minMs, maxMs));
        _maxMs = Math.Max(_minMs, maxMs);
        _distribution = distribution;
        _rng = rng ?? Random.Shared;
    }

    /// <summary>
    /// Returns the next interval in milliseconds within the configured [Min, Max] range.
    /// </summary>
    public int NextIntervalMs()
    {
        if (_minMs >= _maxMs)
        {
            return _minMs;
        }

        if (_distribution == IntervalDistributionKind.TruncatedGaussian)
        {
            return NextTruncatedGaussian();
        }

        // Uniform distribution inclusive of both _minMs and _maxMs
        return _rng.Next(_minMs, _maxMs + 1);
    }

    private int NextTruncatedGaussian()
    {
        double mean = (_minMs + _maxMs) / 2.0;
        // Map the [min, max] span to +/- 3 standard deviations (~99.7% within bounds before clamping)
        double stdDev = (_maxMs - _minMs) / 6.0;

        // Box-Muller transform
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        double stdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

        double sample = mean + stdDev * stdNormal;
        int rounded = (int)Math.Round(sample);
        return Math.Clamp(rounded, _minMs, _maxMs);
    }
}

