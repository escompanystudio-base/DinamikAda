namespace DinamikAda.Core.Physics;

/// <summary>
/// Damped harmonic oscillator with sub-stepped semi-implicit Euler integration
/// and interruptible velocity preservation across mid-flight target changes.
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

    public SpringDamperSolver(double initialValue, double stiffness = 380.0, double dampingRatio = 0.80)
    {
        Value = initialValue;
        Target = initialValue;
        Stiffness = stiffness;
        DampingRatio = dampingRatio;
    }

    public bool IsSettled =>
        Math.Abs(Value - Target) < PositionEpsilon &&
        Math.Abs(Velocity) < VelocityEpsilon &&
        _settledFrames >= 2;

    public void SnapTo(double value)
    {
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
        if (Math.Abs(Target - newTarget) > 1e-5)
        {
            _settledFrames = 0;
        }
        Target = newTarget;
        if (stiffness.HasValue) Stiffness = stiffness.Value;
        if (dampingRatio.HasValue) DampingRatio = dampingRatio.Value;
    }

    public double Step(double rawDtSeconds)
    {
        if (double.IsNaN(rawDtSeconds) || rawDtSeconds <= 0.0)
        {
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
