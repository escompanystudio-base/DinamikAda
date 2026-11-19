namespace DinamikAda.Core.Physics;

/// <summary>
/// Damped harmonic oscillator with sub-stepped semi-implicit Euler integration
/// and interruptible velocity preservation across mid-flight target changes.
/// It can also run as a timed tween (<see cref="AnimateTo"/>): the value eases from wherever it is now to the
/// target over a fixed duration with a caller-supplied curve. Retargeting mid-flight starts a new tween from the
/// current value (snapshot-and-replace), so interruptions never jump.
/// </summary>
public sealed class SpringDamperSolver
{
    private const double MaxRawDt = 0.05;
    private const double MaxSubDt = 1.0 / 480.0;

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; set; }
    public double Stiffness { get; set; }
    public double DampingRatio { get; set; }
    public double Mass { get; set; } = 1.0;
    public double PositionEpsilon { get; set; } = 0.15;
    public double VelocityEpsilon { get; set; } = 0.5;

    private int _settledFrames;

    private Func<double, double>? _tweenEase;
    private double _tweenFrom;
    private double _tweenElapsed;
    private double _tweenDuration;
    private double _tweenDelay;

    /// <summary>True while a timed tween started by <see cref="AnimateTo"/> is still running.</summary>
    public bool IsTweening => _tweenEase is not null;

    public SpringDamperSolver(double initialValue, double stiffness = 380.0, double dampingRatio = 0.80)
    {
        Value = initialValue;
        Target = initialValue;
        Stiffness = stiffness;
        DampingRatio = dampingRatio;
    }

    public bool IsSettled =>
        _tweenEase is null &&
        Math.Abs(Value - Target) < PositionEpsilon &&
        Math.Abs(Velocity) < VelocityEpsilon &&
        _settledFrames >= 2;

    public void SnapTo(double value)
    {
        _tweenEase = null;
        Value = value;
        Target = value;
        Velocity = 0.0;
        _settledFrames = 2;
    }

    public void InjectVelocity(double deltaVelocity, double maxAbsVelocity = 1800.0)
    {
        Velocity = Math.Clamp(Velocity + deltaVelocity, -maxAbsVelocity, maxAbsVelocity);
        _settledFrames = 0;
    }

    public void Retarget(double newTarget, double? stiffness = null, double? dampingRatio = null)
    {
        _tweenEase = null;
        if (Math.Abs(Target - newTarget) > 1e-5)
        {
            _settledFrames = 0;
        }
        Target = newTarget;
        if (stiffness.HasValue) Stiffness = stiffness.Value;
        if (dampingRatio.HasValue) DampingRatio = dampingRatio.Value;
    }

    /// <summary>
    /// Eases from the current value to <paramref name="newTarget"/> over <paramref name="durationSeconds"/> using
    /// <paramref name="ease"/> (0..1 to 0..1, no overshoot expected). Calling it again with the same target while a
    /// tween is already heading there keeps the running tween; a different target restarts from the current value.
    /// </summary>
    public void AnimateTo(double newTarget, double durationSeconds, Func<double, double> ease, double delaySeconds = 0.0)
    {
        ArgumentNullException.ThrowIfNull(ease);
        bool sameTarget = Math.Abs(Target - newTarget) <= 1e-5;
        if (sameTarget && (_tweenEase is not null || Math.Abs(Value - newTarget) <= 1e-5))
        {
            return;
        }

        Target = newTarget;
        if (durationSeconds <= 0.0)
        {
            SnapTo(newTarget);
            return;
        }

        _tweenFrom = Value;
        _tweenElapsed = 0.0;
        _tweenDuration = durationSeconds;
        _tweenDelay = Math.Max(0.0, delaySeconds);
        _tweenEase = ease;
        _settledFrames = 0;
    }

    public double Step(double rawDtSeconds)
    {
        if (double.IsNaN(rawDtSeconds) || rawDtSeconds <= 0.0)
        {
            return Value;
        }

        if (_tweenEase is not null)
        {
            double dtTween = double.IsPositiveInfinity(rawDtSeconds) ? MaxRawDt : Math.Clamp(rawDtSeconds, 0.0, MaxRawDt);
            double before = Value;
            _tweenElapsed += dtTween;
            double t = (_tweenElapsed - _tweenDelay) / _tweenDuration;
            if (t >= 1.0)
            {
                Value = Target;
                Velocity = 0.0;
                _tweenEase = null;
                _settledFrames = 2;
            }
            else if (t > 0.0)
            {
                Value = _tweenFrom + (Target - _tweenFrom) * _tweenEase(t);
                Velocity = dtTween > 0.0 ? (Value - before) / dtTween : 0.0;
            }
            return Value;
        }

        double remaining = double.IsPositiveInfinity(rawDtSeconds) ? MaxRawDt : Math.Clamp(rawDtSeconds, 0.0, MaxRawDt);
        if (remaining <= 0.0)
        {
            return Value;
        }

        double mass = Math.Max(0.05, Mass);
        double stiffness = Math.Max(1.0, Stiffness);
        double damping = Math.Max(0.05, DampingRatio);
        double c = 2.0 * damping * Math.Sqrt(stiffness * mass);

        while (remaining > 0.0)
        {
            double dt = Math.Min(remaining, MaxSubDt);
            double force = -stiffness * (Value - Target) - c * Velocity;
            double accel = force / mass;
            Velocity += accel * dt;
            Value += Velocity * dt;
            remaining -= dt;
        }

        if (Math.Abs(Value - Target) < PositionEpsilon && Math.Abs(Velocity) < VelocityEpsilon)
        {
            _settledFrames++;
            if (_settledFrames >= 2)
            {
                Value = Target;
                Velocity = 0.0;
            }
        }
        else
        {
            _settledFrames = 0;
        }

        return Value;
    }

    public static double SmoothStep(double edge0, double edge1, double x)
    {
        if (Math.Abs(edge1 - edge0) < 1e-9) return x >= edge1 ? 1.0 : 0.0;
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }
}
