using DinamikAda.Core.Abstractions;

namespace DinamikAda.Core.Physics;

/// <summary>
/// Coordinated 240Hz spring rig animating MacBook notch width, height, bottom/top/ear radii,
/// split satellite wing, album-art kinetic pulse, 8-bar asymmetric audio spectrum follower,
/// drag-snap offset, and 3-tier staggered content reveal without hard swaps.
/// </summary>
public sealed class IslandMotionState
{
    public const int SpectrumBarCount = 8;
    private const double SpectrumSettledEpsilon = 0.004;

    public SpringDamperSolver Width { get; } = new(176.0, stiffness: 248.0, dampingRatio: 0.84);
    public SpringDamperSolver Height { get; } = new(28.0, stiffness: 240.0, dampingRatio: 0.85);
    public SpringDamperSolver CornerRadius { get; } = new(13.0, stiffness: 256.0, dampingRatio: 0.86);
    public SpringDamperSolver EarRadius { get; } = new(8.0, stiffness: 260.0, dampingRatio: 0.86)
    {
        PositionEpsilon = 0.005,
        VelocityEpsilon = 0.02
    };
    public SpringDamperSolver TopCornerRadius { get; } = new(0.0, stiffness: 256.0, dampingRatio: 0.86)
    {
        PositionEpsilon = 0.005,
        VelocityEpsilon = 0.02
    };
    public SpringDamperSolver SatelliteWidth { get; } = new(0.0, stiffness: 270.0, dampingRatio: 0.84);
    public SpringDamperSolver SatelliteHeight { get; } = new(28.0, stiffness: 270.0, dampingRatio: 0.86);
    public SpringDamperSolver SatelliteGap { get; } = new(0.0, stiffness: 270.0, dampingRatio: 0.86);
    public SpringDamperSolver SatelliteScale { get; } = new(0.0, stiffness: 270.0, dampingRatio: 0.84)
    {
        PositionEpsilon = 0.008,
        VelocityEpsilon = 0.04
    };
    public SpringDamperSolver HoverScale { get; } = new(1.0, stiffness: 240.0, dampingRatio: 0.92)
    {
        PositionEpsilon = 0.004,
        VelocityEpsilon = 0.02
    };
    public SpringDamperSolver TabIndicatorX { get; } = new(0.0, stiffness: 360.0, dampingRatio: 0.88);
    public SpringDamperSolver TabIndicatorWidth { get; } = new(56.0, stiffness: 360.0, dampingRatio: 0.88);
    public SpringDamperSolver TabContentSlide { get; } = new(1.0, stiffness: 320.0, dampingRatio: 0.92)
    {
        PositionEpsilon = 0.008,
        VelocityEpsilon = 0.04
    };
    public SpringDamperSolver CompactProgress { get; } = new(0.0, stiffness: 340.0, dampingRatio: 0.88)
    {
        PositionEpsilon = 0.005,
        VelocityEpsilon = 0.02
    };
    public SpringDamperSolver CompactContentReveal { get; } = new(1.0, stiffness: 320.0, dampingRatio: 0.92)
    {
        PositionEpsilon = 0.005,
        VelocityEpsilon = 0.02
    };
    public SpringDamperSolver ContentReveal { get; } = new(1.0, stiffness: 255.0, dampingRatio: 0.90)
    {
        PositionEpsilon = 0.004,
        VelocityEpsilon = 0.02
    };
    public SpringDamperSolver AlbumScale { get; } = new(1.0, stiffness: 386.0, dampingRatio: 0.72)
    {
        PositionEpsilon = 0.004,
        VelocityEpsilon = 0.02
    };
    public SpringDamperSolver AlbumAuraPulse { get; } = new(0.0, stiffness: 224.0, dampingRatio: 0.76)
    {
        PositionEpsilon = 0.004,
        VelocityEpsilon = 0.02
    };
    public SpringDamperSolver OffsetX { get; } = new(0.0, stiffness: 362.0, dampingRatio: 0.78);
    public SpringDamperSolver OffsetY { get; } = new(0.0, stiffness: 362.0, dampingRatio: 0.78);
    public SpringDamperSolver Opacity { get; } = new(1.0, stiffness: 400.0, dampingRatio: 0.95)
    {
        PositionEpsilon = 0.005,
        VelocityEpsilon = 0.02
    };

    private readonly double[] _spectrumBars = new double[SpectrumBarCount];
    private readonly double[] _spectrumTargets = new double[SpectrumBarCount];
    private bool _spectrumSettled = true;

    private double _startArea = 176.0 * 28.0;
    private double _targetArea = 176.0 * 28.0;
    private IslandLayoutTarget? _lastTarget;

    public double[] SpectrumBars => _spectrumBars;

    public bool IsAllSettled =>
        Width.IsSettled &&
        Height.IsSettled &&
        CornerRadius.IsSettled &&
        EarRadius.IsSettled &&
        TopCornerRadius.IsSettled &&
        SatelliteWidth.IsSettled &&
        SatelliteHeight.IsSettled &&
        SatelliteGap.IsSettled &&
        SatelliteScale.IsSettled &&
        HoverScale.IsSettled &&
        TabIndicatorX.IsSettled &&
        TabIndicatorWidth.IsSettled &&
        TabContentSlide.IsSettled &&
        CompactProgress.IsSettled &&
        CompactContentReveal.IsSettled &&
        ContentReveal.IsSettled &&
        AlbumScale.IsSettled &&
        AlbumAuraPulse.IsSettled &&
        OffsetX.IsSettled &&
        OffsetY.IsSettled &&
        Opacity.IsSettled &&
        _spectrumSettled;

    public double EffectiveCornerRadius => Math.Min(CornerRadius.Value, Math.Max(2.0, Height.Value * 0.5));

    public double TransitionProgress
    {
        get
        {
            double delta = _targetArea - _startArea;
            if (Math.Abs(delta) < 1.0) return ContentReveal.Value;
            double currentArea = Math.Max(0.0, Width.Value * Height.Value);
            return Math.Clamp((currentArea - _startArea) / delta, 0.0, 1.0);
        }
    }

    public double IncomingContentOpacity => ContentReveal.Target < 0.5
        ? SpringDamperSolver.SmoothStep(0.0, 0.75, Math.Clamp(ContentReveal.Value, 0.0, 1.0))
        : SpringDamperSolver.SmoothStep(0.28, 0.85, Math.Min(TransitionProgress, Math.Clamp(ContentReveal.Value, 0.0, 1.0)));

    public double IncomingContentScale => 0.90 + 0.10 * IncomingContentOpacity;
    public double IncomingContentTranslateY => (1.0 - IncomingContentOpacity) * 8.0;

    public double CompactContentOpacity => SpringDamperSolver.SmoothStep(0.0, 1.0, Math.Clamp(CompactContentReveal.Value, 0.0, 1.0));
    public double ContentRevealTier1 => SpringDamperSolver.SmoothStep(0.18, 0.56, Math.Clamp(ContentReveal.Value, 0.0, 1.0));
    public double ContentRevealTier2 => SpringDamperSolver.SmoothStep(0.24, 0.78, Math.Clamp(ContentReveal.Value, 0.0, 1.0));
    public double ContentRevealTier3 => SpringDamperSolver.SmoothStep(0.36, 0.94, Math.Clamp(ContentReveal.Value, 0.0, 1.0));

    /// <summary>
    /// Compact surfaces use the same opacity curve in both directions so a reversal
    /// starts at exactly the previous frame's opacity.
    /// </summary>
    public double CompactOverlayGate => 1.0 - SpringDamperSolver.SmoothStep(0.02, 0.24, ContentReveal.Value);

    public double TabContentOpacity => SpringDamperSolver.SmoothStep(0.0, 1.0, TabContentSlide.Value);
    public double TabContentTranslateY => (1.0 - TabContentOpacity) * 6.0;
    public double TabContentScale => 0.985 + 0.015 * TabContentOpacity;

    public double SatelliteOpacity => SpringDamperSolver.SmoothStep(0.04, 1.0, SatelliteScale.Value) * CompactOverlayGate;
    public double SatelliteEffectiveScale => 0.90 + 0.10 * Math.Clamp(SatelliteScale.Value, 0.0, 1.0);

    public void SetHoverScale(double targetScale)
    {
        HoverScale.Retarget(Math.Clamp(targetScale, 0.92, 1.08), stiffness: 240.0, dampingRatio: 0.92);
    }

    public void SetCompactProgress(double progress01, bool snap = false)
    {
        double clamped = Math.Clamp(progress01, 0.0, 1.0);
        if (snap) CompactProgress.SnapTo(clamped);
        else CompactProgress.Retarget(clamped, stiffness: 340.0, dampingRatio: 0.88);
    }

    public void RetargetTabIndicator(double x, double width, bool snap = false)
    {
        double w = Math.Max(20.0, width);
        if (snap)
        {
            TabIndicatorX.SnapTo(x);
            TabIndicatorWidth.SnapTo(w);
        }
        else
        {
            TabIndicatorX.Retarget(x, stiffness: 360.0, dampingRatio: 0.88);
            TabIndicatorWidth.Retarget(w, stiffness: 360.0, dampingRatio: 0.88);
        }
    }

    public void TriggerTabTransition()
    {
        TabContentSlide.SnapTo(0.0);
        TabContentSlide.Retarget(1.0, stiffness: 320.0, dampingRatio: 0.92);
    }

    public void TriggerAlbumChange(bool triggerAuraPulse = true)
    {
        AlbumScale.SnapTo(0.84);
        AlbumScale.Retarget(1.0, stiffness: 386.0, dampingRatio: 0.72);
        if (triggerAuraPulse)
        {
            TriggerAlbumAuraPulse();
        }
    }

    public void TriggerAlbumAuraPulse()
    {
        AlbumAuraPulse.SnapTo(1.0);
        AlbumAuraPulse.Retarget(0.0, stiffness: 224.0, dampingRatio: 0.76);
    }

    public void SetAlbumScale(double targetScale, bool snap = false)
    {
        double clamped = Math.Clamp(targetScale, 0.75, 1.15);
        if (snap) AlbumScale.SnapTo(clamped);
        else AlbumScale.Retarget(clamped, stiffness: 386.0, dampingRatio: 0.72);
    }

    public void RetargetOffset(double offsetX, double offsetY, bool snap = false)
    {
        if (snap)
        {
            OffsetX.SnapTo(offsetX);
            OffsetY.SnapTo(offsetY);
        }
        else
        {
            OffsetX.Retarget(offsetX, stiffness: 362.0, dampingRatio: 0.78);
            OffsetY.Retarget(offsetY, stiffness: 362.0, dampingRatio: 0.78);
        }
    }

    public void SetSpectrumTargets(ReadOnlySpan<double> targets)
    {
        bool anyChanged = false;
        int count = Math.Min(SpectrumBarCount, targets.Length);
        for (int i = 0; i < SpectrumBarCount; i++)
        {
            double next = i < count && double.IsFinite(targets[i]) ? Math.Clamp(targets[i], 0.0, 1.0) : 0.0;
            if (Math.Abs(_spectrumTargets[i] - next) > 1e-5 || Math.Abs(_spectrumBars[i] - next) >= SpectrumSettledEpsilon)
            {
                anyChanged = true;
            }
            _spectrumTargets[i] = next;
        }

        if (anyChanged)
        {
            _spectrumSettled = false;
        }
    }

    public void ResetSpectrum(double restingLevel = 0.0)
    {
        double clamped = double.IsFinite(restingLevel) ? Math.Clamp(restingLevel, 0.0, 1.0) : 0.0;
        for (int i = 0; i < SpectrumBarCount; i++)
        {
            _spectrumBars[i] = clamped;
            _spectrumTargets[i] = clamped;
        }
        _spectrumSettled = true;
    }

    public static bool HasSameLayoutGeometry(IslandLayoutTarget? a, IslandLayoutTarget? b)
    {
        if (a is null || b is null) return false;
        return a.State == b.State
            && Math.Abs(a.WidthDip - b.WidthDip) < 0.05
            && Math.Abs(a.HeightDip - b.HeightDip) < 0.05
            && Math.Abs(a.CornerRadiusDip - b.CornerRadiusDip) < 0.05
            && Math.Abs(a.EarRadius - b.EarRadius) < 0.05
            && Math.Abs(a.TopCornerRadius - b.TopCornerRadius) < 0.05
            && Math.Abs(a.SatelliteWidthDip - b.SatelliteWidthDip) < 0.05
            && Math.Abs(a.SatelliteGapDip - b.SatelliteGapDip) < 0.05;
    }

    public void ApplyTarget(IslandLayoutTarget target, bool isExpanding)
    {
        var prevTarget = _lastTarget;
        bool sameGeometry = HasSameLayoutGeometry(prevTarget, target);
        _lastTarget = target;

        if (sameGeometry)
        {
            Opacity.Retarget(target.State == IslandPresentationState.S0Hidden ? 0.0 : 1.0);
            return;
        }

        bool stateChanged = prevTarget is null || prevTarget.State != target.State;
        bool preserveSameOverlayContent = !stateChanged &&
            target.State is IslandPresentationState.S4Expanded or IslandPresentationState.S5Command or IslandPresentationState.S6DragShelf;

        if (preserveSameOverlayContent)
        {
            _startArea = Math.Max(1.0, target.WidthDip * target.HeightDip);
            _targetArea = _startArea;
        }
        else
        {
            _startArea = Math.Max(1.0, Width.Value * Height.Value);
            _targetArea = Math.Max(1.0, target.WidthDip * target.HeightDip);
        }

        if (isExpanding)
        {
            Width.Retarget(target.WidthDip, stiffness: 248.0, dampingRatio: 0.84);
            Height.Retarget(target.HeightDip, stiffness: 240.0, dampingRatio: 0.85);
        }
        else
        {
            Width.Retarget(target.WidthDip, stiffness: 282.0, dampingRatio: 0.88);
            Height.Retarget(target.HeightDip, stiffness: 290.0, dampingRatio: 0.90);
        }

        CornerRadius.Retarget(target.CornerRadiusDip, stiffness: 256.0, dampingRatio: 0.86);
        EarRadius.Retarget(target.EarRadius, stiffness: 260.0, dampingRatio: 0.86);
        TopCornerRadius.Retarget(target.TopCornerRadius, stiffness: 256.0, dampingRatio: 0.86);

        bool hasSat = target.SatelliteWidthDip > 0.5;
        SatelliteWidth.Retarget(target.SatelliteWidthDip, stiffness: 270.0, dampingRatio: 0.84);
        SatelliteHeight.Retarget(hasSat ? Math.Min(36.0, target.HeightDip) : 26.0, stiffness: 270.0, dampingRatio: 0.86);
        SatelliteGap.Retarget(target.SatelliteGapDip, stiffness: 270.0, dampingRatio: 0.86);
        SatelliteScale.Retarget(hasSat ? 1.0 : 0.0, stiffness: hasSat ? 270.0 : 340.0, dampingRatio: hasSat ? 0.84 : 0.92);

        if (target.State == IslandPresentationState.S0Hidden)
        {
            Opacity.Retarget(0.0);
            HoverScale.Retarget(1.0, stiffness: 210.0, dampingRatio: 0.96);
        }
        else
        {
            Opacity.Retarget(1.0);
            if (target.State is IslandPresentationState.S4Expanded or IslandPresentationState.S5Command or IslandPresentationState.S6DragShelf)
            {
                HoverScale.Retarget(1.0, stiffness: 210.0, dampingRatio: 0.96);
            }
        }

        bool isOverlayState = target.State is IslandPresentationState.S3TransientHud
            or IslandPresentationState.S4Expanded
            or IslandPresentationState.S5Command
            or IslandPresentationState.S6DragShelf;

        if (isOverlayState)
        {
            if (stateChanged && ContentReveal.Value <= 0.04)
            {
                ContentReveal.SnapTo(0.0);
            }
            ContentReveal.Retarget(1.0, stiffness: 255.0, dampingRatio: 0.90);
            CompactContentReveal.Retarget(target.State == IslandPresentationState.S3TransientHud ? 1.0 : 0.0, stiffness: 320.0, dampingRatio: 0.92);
        }
        else
        {
            ContentReveal.Retarget(0.0, stiffness: 310.0, dampingRatio: 0.94);
            if (target.State == IslandPresentationState.S0Hidden)
            {
                CompactContentReveal.Retarget(0.0, stiffness: 320.0, dampingRatio: 0.92);
            }
            else
            {
                CompactContentReveal.Retarget(1.0, stiffness: 340.0, dampingRatio: 0.92);
            }
        }
    }

    public void StepAll(double rawDtSeconds)
    {
        Width.Step(rawDtSeconds);
        Height.Step(rawDtSeconds);
        CornerRadius.Step(rawDtSeconds);
        EarRadius.Step(rawDtSeconds);
        TopCornerRadius.Step(rawDtSeconds);
        SatelliteWidth.Step(rawDtSeconds);
        SatelliteHeight.Step(rawDtSeconds);
        SatelliteGap.Step(rawDtSeconds);
        SatelliteScale.Step(rawDtSeconds);
        HoverScale.Step(rawDtSeconds);
        TabIndicatorX.Step(rawDtSeconds);
        TabIndicatorWidth.Step(rawDtSeconds);
        TabContentSlide.Step(rawDtSeconds);
        CompactProgress.Step(rawDtSeconds);
        CompactContentReveal.Step(rawDtSeconds);
        ContentReveal.Step(rawDtSeconds);
        AlbumScale.Step(rawDtSeconds);
        AlbumAuraPulse.Step(rawDtSeconds);
        OffsetX.Step(rawDtSeconds);
        OffsetY.Step(rawDtSeconds);
        Opacity.Step(rawDtSeconds);
        StepSpectrum(rawDtSeconds);
    }

    private void StepSpectrum(double rawDtSeconds)
    {
        if (_spectrumSettled || double.IsNaN(rawDtSeconds) || rawDtSeconds <= 0.0)
        {
            return;
        }

        double dt = double.IsPositiveInfinity(rawDtSeconds) ? 0.05 : Math.Clamp(rawDtSeconds, 0.0, 0.05);
        if (dt <= 0.0)
        {
            return;
        }

        double attackAlpha = 1.0 - Math.Exp(-dt * 28.0);
        double decayAlpha = 1.0 - Math.Exp(-dt * 11.0);
        bool allSettled = true;

        for (int i = 0; i < SpectrumBarCount; i++)
        {
            double current = _spectrumBars[i];
            double target = _spectrumTargets[i];
            double diff = target - current;
            if (Math.Abs(diff) < SpectrumSettledEpsilon)
            {
                _spectrumBars[i] = target;
                continue;
            }

            double alpha = diff > 0.0 ? attackAlpha : decayAlpha;
            double next = current + diff * alpha;
            if (Math.Abs(target - next) < SpectrumSettledEpsilon)
            {
                _spectrumBars[i] = target;
            }
            else
            {
                _spectrumBars[i] = next;
                allSettled = false;
            }
        }

        _spectrumSettled = allSettled;
    }
}
