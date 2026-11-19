using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DinamikAda.Core.Voice;

namespace DinamikAda.App.Views.Voice;

/// <summary>What the assistant character is doing.</summary>
public enum MascotState
{
    Idle = 0,
    Listening = 1,
    Working = 2,
    Talking = 3,
    Error = 4
}

/// <summary>
/// DinamikAda's own assistant character ("Ada" by default): a rounded blob drawn with WPF geometry on a 120×120
/// design canvas that scales to any size. Every motion is a WPF animation (no per-frame loops); blinking is a
/// randomized timer that triggers a short animation.
/// </summary>
public sealed class MascotControl : Viewbox
{
    private const double Cx = 60;
    private const double FootY = 104;
    private static readonly Point LeftEye = new(46, 63);
    private static readonly Point RightEye = new(74, 63);

    private readonly Canvas _canvas = new() { Width = 120, Height = 120, ClipToBounds = false };
    private readonly Ellipse _glow = new() { Width = 116, Height = 116, IsHitTestVisible = false };
    private readonly Ellipse _ring = new() { Width = 96, Height = 96, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Ellipse _shadow = new() { Width = 56, Height = 10, IsHitTestVisible = false };
    private readonly Canvas _body = new() { Width = 120, Height = 120 };
    private readonly Path _bodyShape = new();
    private readonly Path _bodyShade = new() { IsHitTestVisible = false };
    private readonly Ellipse _highlight = new() { Width = 20, Height = 12 };
    private readonly Ellipse _sparkDot = new() { Width = 4.5, Height = 4.5 };
    private readonly Ellipse _cheekL = new() { Width = 10, Height = 5.5 };
    private readonly Ellipse _cheekR = new() { Width = 10, Height = 5.5 };
    private readonly Canvas _eyes = new();
    private readonly Canvas _mouth = new();
    private readonly Path _smile = new() { StrokeThickness = 2.3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Canvas _openMouth = new();
    private readonly Canvas _accessory = new();
    private readonly Canvas _dots = new() { Width = 120, Height = 120, IsHitTestVisible = false };

    private readonly ScaleTransform _squash = new(1, 1, Cx, FootY);
    private readonly RotateTransform _tilt = new(0, Cx, FootY);
    private readonly TranslateTransform _move = new();
    private readonly ScaleTransform _blink = new(1, 1, Cx, LeftEye.Y);
    private readonly TranslateTransform _look = new();
    private readonly ScaleTransform _mouthOpen = new(1, 0.3, 0, -1);
    private readonly ScaleTransform _glowScale = new(1, 1, 58, 58);
    private readonly ScaleTransform _ringScale = new(1, 1, 48, 48);
    private readonly ScaleTransform _shadowScale = new(1, 1, 28, 5);
    private readonly RotateTransform _orbit = new(0, Cx, 62);

    private readonly DispatcherTimer _blinkTimer = new(DispatcherPriority.Background);
    private readonly Random _rng = new();
    private MascotSettings _settings = new();
    private MascotState _state = MascotState.Idle;
    private double _level;
    private int _stateToken;
    private Ellipse? _antennaHalo;
    private Color _base, _light, _dark, _ink;

    public MascotControl()
    {
        Stretch = Stretch.Uniform;
        Child = _canvas;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;

        Canvas.SetLeft(_glow, Cx - 58);
        Canvas.SetTop(_glow, 64 - 58);
        _glow.RenderTransform = _glowScale;
        _glow.Opacity = 0;
        Canvas.SetLeft(_ring, Cx - 48);
        Canvas.SetTop(_ring, 64 - 48);
        _ring.RenderTransform = _ringScale;
        _ring.Opacity = 0;
        Canvas.SetLeft(_shadow, Cx - 28);
        Canvas.SetTop(_shadow, FootY - 3);
        _shadow.RenderTransform = _shadowScale;

        _bodyShape.Data = BodyGeometry;
        _bodyShape.StrokeThickness = 1.1;
        _bodyShade.Data = BodyGeometry;
        Canvas.SetLeft(_highlight, 30);
        Canvas.SetTop(_highlight, 27);
        _highlight.RenderTransform = new RotateTransform(-30, 10, 6);
        Canvas.SetLeft(_sparkDot, 50);
        Canvas.SetTop(_sparkDot, 25);
        Canvas.SetLeft(_cheekL, 31);
        Canvas.SetTop(_cheekL, 72);
        Canvas.SetLeft(_cheekR, 79);
        Canvas.SetTop(_cheekR, 72);

        var eyeGroup = new TransformGroup();
        eyeGroup.Children.Add(_blink);
        eyeGroup.Children.Add(_look);
        _eyes.RenderTransform = eyeGroup;

        Canvas.SetLeft(_mouth, Cx);
        Canvas.SetTop(_mouth, 79);
        _mouth.Children.Add(_smile);
        _openMouth.RenderTransform = _mouthOpen;
        _openMouth.Opacity = 0;
        _mouth.Children.Add(_openMouth);

        var bodyGroup = new TransformGroup();
        bodyGroup.Children.Add(_squash);
        bodyGroup.Children.Add(_tilt);
        bodyGroup.Children.Add(_move);
        _body.RenderTransform = bodyGroup;
        _body.Children.Add(_bodyShape);
        _body.Children.Add(_bodyShade);
        _body.Children.Add(_highlight);
        _body.Children.Add(_sparkDot);
        _body.Children.Add(_cheekL);
        _body.Children.Add(_cheekR);
        _body.Children.Add(_eyes);
        _body.Children.Add(_mouth);
        _body.Children.Add(_accessory);

        _dots.RenderTransform = _orbit;
        _dots.Opacity = 0;

        _canvas.Children.Add(_glow);
        _canvas.Children.Add(_ring);
        _canvas.Children.Add(_shadow);
        _canvas.Children.Add(_body);
        _canvas.Children.Add(_dots);

        _blinkTimer.Tick += (_, _) => { Blink(); ScheduleBlink(); };
        Loaded += (_, _) => { if (CanAnimate) { ScheduleBlink(); ApplyState(); } };
        Unloaded += (_, _) => _blinkTimer.Stop();

        Apply(_settings);
    }

    /// <summary>False renders a still pose for each state (render audit, screenshots).</summary>
    public bool AnimationsEnabled { get; set; } = true;

    private bool CanAnimate => AnimationsEnabled && SystemParameters.ClientAreaAnimation;

    public MascotState State
    {
        get => _state;
        set
        {
            if (_state == value && !_frozen) return;
            _state = value;
            _frozen = false;
            ApplyState();
        }
    }

    private bool _frozen;

    /// <summary>Stops every running animation and the blink timer (call while the host is hidden).</summary>
    public void Freeze()
    {
        _stateToken++;
        _frozen = true;
        _state = MascotState.Idle;
        _blinkTimer.Stop();
        StopAll();
        BuildEyes();
        BuildMouth();
        if (_antennaHalo is not null)
        {
            _antennaHalo.BeginAnimation(OpacityProperty, null);
            if (_antennaHalo.RenderTransform is ScaleTransform halo)
            {
                halo.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                halo.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
        }
    }

    /// <summary>Microphone level (0..1) while listening; smoothed so the body squashes softly.</summary>
    public void SetLevel(double level01)
    {
        double v = double.IsFinite(level01) ? Math.Clamp(level01, 0, 1) : 0;
        _level = v > _level ? _level + (v - _level) * 0.65 : _level + (v - _level) * 0.3;
        if (_state != MascotState.Listening) return;
        ApplyLevel(CanAnimate);
    }

    /// <summary>Updates the look live (colour, eyes, accessory).</summary>
    public void Apply(MascotSettings settings)
    {
        _settings = settings?.Clone() ?? new MascotSettings();
        _base = ParseColor(_settings.ColorHex);
        _light = Mix(_base, Colors.White, 0.45);
        _dark = Mix(_base, Colors.Black, 0.38);
        _ink = Luminance(_base) < 0.22 ? Color.FromRgb(0xF6, 0xF3, 0xFF) : Color.FromRgb(0x22, 0x1A, 0x36);

        _bodyShape.Fill = Frozen(new RadialGradientBrush
        {
            GradientOrigin = new Point(0.34, 0.24),
            Center = new Point(0.42, 0.34),
            RadiusX = 0.78,
            RadiusY = 0.78,
            GradientStops =
            {
                new GradientStop(_light, 0),
                new GradientStop(_base, 0.52),
                new GradientStop(_dark, 1)
            }
        });
        _bodyShape.Stroke = Frozen(new SolidColorBrush(Color.FromArgb(0x90, _dark.R, _dark.G, _dark.B)));
        _bodyShade.Fill = Frozen(new LinearGradientBrush(
            Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0x30, 0, 0, 0), new Point(0.5, 0.55), new Point(0.5, 1)));
        _highlight.Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0xD0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)));
        _sparkDot.Fill = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255)));
        var cheek = Frozen(new RadialGradientBrush(Color.FromArgb(0x80, 0xFF, 0x6F, 0xA0), Color.FromArgb(0, 0xFF, 0x6F, 0xA0)));
        _cheekL.Fill = cheek;
        _cheekR.Fill = cheek;
        _shadow.Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0x70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)));
        _glow.Fill = Frozen(new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x90, _light.R, _light.G, _light.B), 0.35),
                new GradientStop(Color.FromArgb(0x38, _base.R, _base.G, _base.B), 0.7),
                new GradientStop(Color.FromArgb(0, _base.R, _base.G, _base.B), 1)
            }
        });
        _ring.Stroke = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, _light.R, _light.G, _light.B)));

        BuildEyes();
        BuildMouth();
        BuildAccessory();
        BuildDots();
        if (CanAnimate && IsLoaded && !_frozen) StartAccessoryLoop();
    }

    // ---- geometry ---------------------------------------------------------------------------------------

    private static readonly Geometry BodyGeometry = FrozenGeometry(
        "M60,20 C85,20 100,40 100.5,66 C101,90 86,104 60,104 C34,104 19,90 19.5,66 C20,40 35,20 60,20 Z");

    private void BuildEyes()
    {
        _eyes.Children.Clear();
        bool worried = _state == MascotState.Error;
        var ink = Frozen(new SolidColorBrush(_ink));
        var shine = Frozen(new SolidColorBrush(_ink.R > 128 ? Color.FromArgb(0xE0, _dark.R, _dark.G, _dark.B) : Color.FromRgb(255, 255, 255)));

        foreach (var (c, isRight) in new[] { (LeftEye, false), (RightEye, true) })
        {
            if (worried)
            {
                AddEllipse(_eyes, c, 4.5, 5.6, ink);
                AddEllipse(_eyes, new Point(c.X + 1.4, c.Y - 2), 1.4, 1.4, shine);
                // Brows raised toward the middle.
                if (isRight) AddStroke(_eyes, $"M{c.X - 5},{c.Y - 11.5} L{c.X + 6},{c.Y - 8}", ink, 2.2);
                else AddStroke(_eyes, $"M{c.X - 6},{c.Y - 8} L{c.X + 5},{c.Y - 11.5}", ink, 2.2);
                continue;
            }
            switch (_settings.EyeStyle)
            {
                case 1: // sleepy
                {
                    // Heavy lids drooping toward the outer corners: drowsy, not cross.
                    double o = isRight ? 1 : -1;
                    AddPath(_eyes, $"M{c.X - 5.2},{c.Y + 0.6} A5.2,4.4 0 0 0 {c.X + 5.2},{c.Y + 0.6} Z", ink);
                    AddStroke(_eyes, $"M{c.X - 6.8 * o},{c.Y - 0.6} Q{c.X},{c.Y - 1.6} {c.X + 6.8 * o},{c.Y + 1.6}", ink, 2.1);
                    AddEllipse(_eyes, new Point(c.X + 1.6, c.Y + 2.4), 1, 1, shine);
                }
                    break;
                case 2: // happy arcs
                    AddStroke(_eyes, $"M{c.X - 6},{c.Y + 2.5} Q{c.X},{c.Y - 6} {c.X + 6},{c.Y + 2.5}", ink, 2.8);
                    break;
                case 3: // sparkle
                    AddEllipse(_eyes, c, 6, 7.2, ink);
                    AddEllipse(_eyes, new Point(c.X + 2, c.Y - 2.7), 2.4, 2.4, shine);
                    AddEllipse(_eyes, new Point(c.X - 2.2, c.Y + 2.6), 1.1, 1.1, shine);
                    if (isRight)
                    {
                        var star = new Path
                        {
                            Data = FrozenGeometry("M0,-3.6 Q0.6,-0.6 3.6,0 Q0.6,0.6 0,3.6 Q-0.6,0.6 -3.6,0 Q-0.6,-0.6 0,-3.6 Z"),
                            Fill = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xB8)))
                        };
                        Canvas.SetLeft(star, c.X + 10);
                        Canvas.SetTop(star, c.Y - 11);
                        _eyes.Children.Add(star);
                    }
                    break;
                default: // round
                    AddEllipse(_eyes, c, 5, 6.2, ink);
                    AddEllipse(_eyes, new Point(c.X + 1.8, c.Y - 2.4), 1.8, 1.8, shine);
                    break;
            }
        }
    }

    private void BuildMouth()
    {
        var ink = Frozen(new SolidColorBrush(_ink));
        _smile.Stroke = ink;
        _smile.Data = FrozenGeometry(_state == MascotState.Error ? "M-4.5,3 Q0,-1.6 4.5,3" : "M-5,0 Q0,4.6 5,0");
        _openMouth.Children.Clear();
        _openMouth.Children.Add(new Path { Data = FrozenGeometry("M-5,-1 Q0,-1.8 5,-1 Q4.6,6 0,6.6 Q-4.6,6 -5,-1 Z"), Fill = ink });
        var tongue = new Ellipse { Width = 6, Height = 3, Fill = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x7E, 0x9D))) };
        Canvas.SetLeft(tongue, -3);
        Canvas.SetTop(tongue, 2.8);
        _openMouth.Children.Add(tongue);
    }

    private void BuildAccessory()
    {
        _accessory.Children.Clear();
        _antennaHalo = null;
        var dark = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x33, 0x4F)));
        switch (_settings.Accessory)
        {
            case 1: // headphones
            {
                AddStroke(_accessory, $"M22,60 C20,14 100,14 98,60", dark, 5.6);
                AddStroke(_accessory, $"M25,52 C26,22 94,22 95,52", Frozen(new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255))), 1.3);
                var cup = Frozen(new LinearGradientBrush(Color.FromRgb(0x46, 0x3E, 0x5E), Color.FromRgb(0x1C, 0x18, 0x29), 90));
                var pad = Frozen(new SolidColorBrush(_light));
                foreach (double x in new[] { 13.0, 94.0 })
                {
                    var r = new Rectangle { Width = 13, Height = 25, RadiusX = 6.5, RadiusY = 6.5, Fill = cup };
                    Canvas.SetLeft(r, x);
                    Canvas.SetTop(r, 50);
                    _accessory.Children.Add(r);
                    AddEllipse(_accessory, new Point(x + 6.5, 62.5), 2.2, 6, pad);
                }
                break;
            }
            case 2: // bow
            {
                var bow = new Canvas { RenderTransform = new RotateTransform(18) };
                var fill = Frozen(new LinearGradientBrush(Color.FromRgb(0xFF, 0x8F, 0xB8), Color.FromRgb(0xF0, 0x3E, 0x7C), 90));
                var edge = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0xA8, 0x1E, 0x52)));
                bow.Children.Add(new Path { Data = FrozenGeometry("M0,0 C-6,-9 -17,-7 -15,2 C-13,10 -5,6 0,0 Z"), Fill = fill, Stroke = edge, StrokeThickness = 0.9 });
                bow.Children.Add(new Path { Data = FrozenGeometry("M0,0 C6,-9 17,-7 15,2 C13,10 5,6 0,0 Z"), Fill = fill, Stroke = edge, StrokeThickness = 0.9 });
                AddEllipse(bow, new Point(0, 0), 3.6, 3.6, Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x34, 0x72))));
                AddEllipse(bow, new Point(-8, -2.5), 2, 1.2, Frozen(new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255))));
                Canvas.SetLeft(bow, 82);
                Canvas.SetTop(bow, 27);
                _accessory.Children.Add(bow);
                break;
            }
            case 3: // antenna with glowing tip
            {
                AddStroke(_accessory, $"M60,21 C60,14 63,10 66.5,6.5", Frozen(new SolidColorBrush(Mix(_dark, Colors.White, 0.25))), 2.6);
                var gold = Color.FromRgb(0xFF, 0xD1, 0x4A);
                _antennaHalo = new Ellipse
                {
                    Width = 20, Height = 20,
                    Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0xA0, gold.R, gold.G, gold.B), Color.FromArgb(0, gold.R, gold.G, gold.B))),
                    RenderTransform = new ScaleTransform(1, 1, 10, 10)
                };
                Canvas.SetLeft(_antennaHalo, 67.5 - 10);
                Canvas.SetTop(_antennaHalo, 5.5 - 10);
                _accessory.Children.Add(_antennaHalo);
                var tip = new Ellipse
                {
                    Width = 8.5, Height = 8.5,
                    Fill = Frozen(new RadialGradientBrush(Color.FromRgb(0xFF, 0xFB, 0xD8), gold) { GradientOrigin = new Point(0.35, 0.3) }),
                    Effect = new DropShadowEffect { Color = gold, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.95 }
                };
                Canvas.SetLeft(tip, 67.5 - 4.25);
                Canvas.SetTop(tip, 5.5 - 4.25);
                _accessory.Children.Add(tip);
                break;
            }
            case 4: // glasses
            {
                var frame = Frozen(new SolidColorBrush(_ink));
                var lens = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)));
                foreach (var c in new[] { LeftEye, RightEye })
                {
                    var e = new Ellipse { Width = 19, Height = 19, Stroke = frame, StrokeThickness = 2.1, Fill = lens };
                    Canvas.SetLeft(e, c.X - 9.5);
                    Canvas.SetTop(e, c.Y - 9.5);
                    _accessory.Children.Add(e);
                }
                AddStroke(_accessory, $"M55.5,62 Q60,59.5 64.5,62", frame, 2);
                AddStroke(_accessory, $"M36.5,61 L26,58.5", frame, 2);
                AddStroke(_accessory, $"M83.5,61 L94,58.5", frame, 2);
                AddStroke(_accessory, $"M40,57.5 Q42,55.5 45,55.6", Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255))), 1.3);
                break;
            }
        }
    }

    private void BuildDots()
    {
        _dots.Children.Clear();
        double[] angles = [-112, -84, -56];
        double[] alpha = [1, 0.72, 0.44];
        for (int i = 0; i < angles.Length; i++)
        {
            double a = angles[i] * Math.PI / 180;
            var p = new Point(Cx + Math.Cos(a) * 52, 62 + Math.Sin(a) * 52);
            var dot = new Ellipse
            {
                Width = 8 - i, Height = 8 - i,
                Fill = Frozen(new SolidColorBrush(_light)),
                Opacity = alpha[i],
                Effect = i == 0 ? new DropShadowEffect { Color = _light, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.8 } : null
            };
            Canvas.SetLeft(dot, p.X - dot.Width / 2);
            Canvas.SetTop(dot, p.Y - dot.Height / 2);
            _dots.Children.Add(dot);
        }
    }

    // ---- states -----------------------------------------------------------------------------------------

    private void ApplyState()
    {
        int token = ++_stateToken;
        StopAll();
        BuildEyes();
        BuildMouth();
        bool anim = CanAnimate;
        if (anim) StartAccessoryLoop();
        if (anim && IsLoaded && !_blinkTimer.IsEnabled) ScheduleBlink();

        switch (_state)
        {
            case MascotState.Idle:
                if (anim) StartBob(3.0, 1700);
                break;

            case MascotState.Listening:
                if (anim)
                {
                    Run(_tilt, RotateTransform.AngleProperty, To(-6, 320, Ease()));
                    Run(_move, TranslateTransform.YProperty, To(-2, 320, Ease()));
                    var pulseScale = new DoubleAnimation(0.86, 1.18, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Ease() };
                    Run(_ringScale, ScaleTransform.ScaleXProperty, pulseScale);
                    Run(_ringScale, ScaleTransform.ScaleYProperty, pulseScale);
                    Run(_ring, OpacityProperty, new DoubleAnimation(0.7, 0, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever });
                }
                else
                {
                    _tilt.Angle = -6;
                    _move.Y = -2;
                    _ringScale.ScaleX = _ringScale.ScaleY = 1.06;
                    _ring.Opacity = 0.35;
                }
                ApplyLevel(anim);
                break;

            case MascotState.Working:
                if (anim)
                {
                    Run(_dots, OpacityProperty, To(1, 220));
                    Run(_orbit, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1500)) { RepeatBehavior = RepeatBehavior.Forever });
                    Run(_look, TranslateTransform.YProperty, To(-3.4, 260, Ease()));
                    Run(_look, TranslateTransform.XProperty, new DoubleAnimation(-1.6, 1.6, TimeSpan.FromMilliseconds(1400)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Ease(EasingMode.EaseInOut) });
                    Run(_tilt, RotateTransform.AngleProperty, new DoubleAnimation(-3, 3, TimeSpan.FromMilliseconds(1400)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Ease(EasingMode.EaseInOut) });
                }
                else
                {
                    _dots.Opacity = 1;
                    _look.Y = -3.4;
                    _look.X = 1.4;
                    _tilt.Angle = 2;
                }
                break;

            case MascotState.Talking:
                if (anim)
                {
                    _openMouth.Opacity = 1;
                    _smile.Opacity = 0;
                    var chatter = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(150))
                    {
                        AutoReverse = true,
                        RepeatBehavior = new RepeatBehavior(9),
                        EasingFunction = Ease(EasingMode.EaseInOut)
                    };
                    chatter.Completed += (_, _) =>
                    {
                        if (token != _stateToken) return;
                        _openMouth.Opacity = 0;
                        _smile.Opacity = 1;
                    };
                    Run(_mouthOpen, ScaleTransform.ScaleYProperty, chatter);
                    var hop = new DoubleAnimationUsingKeyFrames();
                    hop.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), Ease()));
                    hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(640)), new BounceEase { Bounces = 2, Bounciness = 2.6, EasingMode = EasingMode.EaseOut }));
                    hop.Completed += (_, _) => { if (token == _stateToken) StartBob(2.4, 1500); };
                    Run(_move, TranslateTransform.YProperty, hop);
                    var squish = new DoubleAnimationUsingKeyFrames();
                    squish.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
                    squish.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), Ease()));
                    squish.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), Ease()));
                    Run(_squash, ScaleTransform.ScaleYProperty, squish);
                }
                else
                {
                    _openMouth.Opacity = 1;
                    _smile.Opacity = 0;
                    _mouthOpen.ScaleY = 0.85;
                    _move.Y = -4;
                }
                break;

            case MascotState.Error:
                if (anim)
                {
                    var shake = new DoubleAnimationUsingKeyFrames();
                    double[] xs = [-5, 5, -4, 4, -2, 1.5, 0];
                    for (int i = 0; i < xs.Length; i++)
                        shake.KeyFrames.Add(new EasingDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(65 * (i + 1)))));
                    Run(_move, TranslateTransform.XProperty, shake);
                    Run(_tilt, RotateTransform.AngleProperty, To(4, 380, Ease()));
                    Run(_squash, ScaleTransform.ScaleYProperty, To(0.96, 380, Ease()));
                }
                else
                {
                    _tilt.Angle = 4;
                    _squash.ScaleY = 0.96;
                }
                break;
        }
    }

    private void ApplyLevel(bool anim)
    {
        double l = _level;
        double sy = 1 - 0.09 * l, sx = 1 + 0.07 * l;
        double glowO = 0.28 + 0.62 * l, glowS = 0.9 + 0.26 * l;
        if (anim)
        {
            var d = TimeSpan.FromMilliseconds(110);
            Run(_squash, ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, d), HandoffBehavior.SnapshotAndReplace);
            Run(_squash, ScaleTransform.ScaleYProperty, new DoubleAnimation(sy, d), HandoffBehavior.SnapshotAndReplace);
            Run(_glow, OpacityProperty, new DoubleAnimation(glowO, d), HandoffBehavior.SnapshotAndReplace);
            Run(_glowScale, ScaleTransform.ScaleXProperty, new DoubleAnimation(glowS, d), HandoffBehavior.SnapshotAndReplace);
            Run(_glowScale, ScaleTransform.ScaleYProperty, new DoubleAnimation(glowS, d), HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            _squash.ScaleX = sx;
            _squash.ScaleY = sy;
            _glow.Opacity = glowO;
            _glowScale.ScaleX = _glowScale.ScaleY = glowS;
        }
    }

    private void StartBob(double amplitude, double ms)
    {
        var bob = new DoubleAnimation(0, -amplitude, TimeSpan.FromMilliseconds(ms))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Ease(EasingMode.EaseInOut)
        };
        Run(_move, TranslateTransform.YProperty, bob);
        var shadow = new DoubleAnimation(1, 0.86, TimeSpan.FromMilliseconds(ms))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Ease(EasingMode.EaseInOut)
        };
        Run(_shadowScale, ScaleTransform.ScaleXProperty, shadow);
    }

    private void StartAccessoryLoop()
    {
        if (_antennaHalo?.RenderTransform is not ScaleTransform halo) return;
        var pulse = new DoubleAnimation(0.8, 1.35, TimeSpan.FromMilliseconds(1100))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Ease(EasingMode.EaseInOut)
        };
        halo.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        halo.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        _antennaHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(1100))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private void StopAll()
    {
        foreach (var (o, dp) in new (Animatable, DependencyProperty)[]
        {
            (_squash, ScaleTransform.ScaleXProperty), (_squash, ScaleTransform.ScaleYProperty),
            (_tilt, RotateTransform.AngleProperty), (_move, TranslateTransform.XProperty), (_move, TranslateTransform.YProperty),
            (_look, TranslateTransform.XProperty), (_look, TranslateTransform.YProperty),
            (_mouthOpen, ScaleTransform.ScaleYProperty), (_glowScale, ScaleTransform.ScaleXProperty), (_glowScale, ScaleTransform.ScaleYProperty),
            (_ringScale, ScaleTransform.ScaleXProperty), (_ringScale, ScaleTransform.ScaleYProperty),
            (_shadowScale, ScaleTransform.ScaleXProperty), (_orbit, RotateTransform.AngleProperty)
        })
        {
            o.BeginAnimation(dp, null);
        }
        foreach (UIElement e in new UIElement[] { _glow, _ring, _dots })
            e.BeginAnimation(OpacityProperty, null);

        _squash.ScaleX = _squash.ScaleY = 1;
        _tilt.Angle = 0;
        _move.X = _move.Y = 0;
        _look.X = _look.Y = 0;
        _mouthOpen.ScaleY = 0.3;
        _glowScale.ScaleX = _glowScale.ScaleY = 1;
        _ringScale.ScaleX = _ringScale.ScaleY = 1;
        _shadowScale.ScaleX = 1;
        _orbit.Angle = 0;
        _glow.Opacity = 0;
        _ring.Opacity = 0;
        _dots.Opacity = 0;
        _openMouth.Opacity = 0;
        _smile.Opacity = 1;
    }

    private void ScheduleBlink()
    {
        if (!CanAnimate) return;
        _blinkTimer.Stop();
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(_rng.Next(3000, 6000));
        _blinkTimer.Start();
    }

    private void Blink()
    {
        if (!IsLoaded || !IsVisible) return;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), Ease(EasingMode.EaseIn)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)), Ease()));
        _blink.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static void Run(Animatable target, DependencyProperty dp, AnimationTimeline anim, HandoffBehavior h = HandoffBehavior.SnapshotAndReplace)
        => target.BeginAnimation(dp, anim, h);

    private static void Run(UIElement target, DependencyProperty dp, AnimationTimeline anim, HandoffBehavior h = HandoffBehavior.SnapshotAndReplace)
        => target.BeginAnimation(dp, anim, h);

    private static DoubleAnimation To(double to, double ms, IEasingFunction? ease = null)
        => new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };

    private static IEasingFunction Ease(EasingMode mode = EasingMode.EaseOut) => new SineEase { EasingMode = mode };

    private static void AddEllipse(Canvas host, Point c, double rx, double ry, Brush fill)
    {
        var e = new Ellipse { Width = rx * 2, Height = ry * 2, Fill = fill };
        Canvas.SetLeft(e, c.X - rx);
        Canvas.SetTop(e, c.Y - ry);
        host.Children.Add(e);
    }

    private static void AddPath(Canvas host, FormattableString data, Brush fill)
        => host.Children.Add(new Path { Data = FrozenGeometry(FormattableString.Invariant(data)), Fill = fill });

    private static void AddStroke(Canvas host, FormattableString data, Brush stroke, double thickness)
        => host.Children.Add(new Path
        {
            Data = FrozenGeometry(FormattableString.Invariant(data)),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

    private static Geometry FrozenGeometry(string data)
    {
        var g = Geometry.Parse(data);
        if (g.CanFreeze) g.Freeze();
        return g;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        if (f.CanFreeze) f.Freeze();
        return f;
    }

    internal static Color ParseColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex.Trim().StartsWith('#') ? hex.Trim() : "#" + hex.Trim()) is Color c)
                return Color.FromRgb(c.R, c.G, c.B);
        }
        catch (FormatException) { }
        return Color.FromRgb(0x7C, 0x5C, 0xFF);
    }

    internal static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}

/// <summary>Still renders of the mascot for the render audit.</summary>
public static class MascotGallery
{
    /// <summary>Every eye style × accessory in three colours, plus a strip of the five states.</summary>
    public static System.Windows.Media.Imaging.BitmapSource RenderVariantsForAudit()
    {
        const double cell = 64, labelW = 64, gap = 28;
        string[] colours = ["#7C5CFF", "#2FD69A", "#FF7A59"];
        string[] eyes = ["Round", "Sleepy", "Happy", "Sparkle"];
        string[] accs = ["None", "Phones", "Bow", "Antenna", "Glasses"];
        double panelW = accs.Length * cell;
        double width = labelW + colours.Length * panelW + (colours.Length - 1) * gap + 24;
        double gridH = 28 + eyes.Length * cell;
        double height = 20 + gridH + 34 + 112 + 20;

        var root = new Canvas { Width = width, Height = height, Background = new LinearGradientBrush(Color.FromRgb(0x17, 0x15, 0x22), Color.FromRgb(0x0E, 0x0D, 0x15), 90) };
        TextBlock Label(string t, double size = 10.5, byte a = 0xA0) => new()
        {
            Text = t,
            FontSize = size,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromArgb(a, 0xF0, 0xEE, 0xF8))
        };
        void Put(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); root.Children.Add(e); }

        for (int r = 0; r < eyes.Length; r++) Put(Label(eyes[r]), 14, 20 + 28 + r * cell + cell / 2 - 8);
        for (int p = 0; p < colours.Length; p++)
        {
            double x0 = labelW + p * (panelW + gap);
            var panel = new Border
            {
                Width = panelW + 8, Height = gridH + 4, CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255)), BorderThickness = new Thickness(1)
            };
            Put(panel, x0 - 4, 16);
            for (int c = 0; c < accs.Length; c++) Put(Label(accs[c], 10), x0 + c * cell + 12, 22);
            for (int r = 0; r < eyes.Length; r++)
                for (int c = 0; c < accs.Length; c++)
                {
                    var m = new MascotControl { AnimationsEnabled = false, Width = cell, Height = cell };
                    m.Apply(new MascotSettings { ColorHex = colours[p], EyeStyle = r, Accessory = c });
                    Put(m, x0 + c * cell, 20 + 28 + r * cell);
                }
        }

        double sy = 20 + gridH + 34;
        Put(Label("States", 11.5, 0xD0), 14, sy - 22);
        var states = Enum.GetValues<MascotState>();
        for (int i = 0; i < states.Length; i++)
        {
            var m = new MascotControl { AnimationsEnabled = false, Width = 96, Height = 96 };
            m.Apply(new MascotSettings { Accessory = i == 2 ? 3 : i == 3 ? 2 : i == 4 ? 4 : 1, EyeStyle = i == 3 ? 3 : 0, ColorHex = i == 1 ? "#3DB8FF" : "#7C5CFF" });
            m.State = states[i];
            if (states[i] == MascotState.Listening) { m.SetLevel(0.9); m.SetLevel(0.9); m.SetLevel(0.9); }
            double x = labelW + i * 150;
            Put(m, x, sy);
            Put(Label(states[i].ToString(), 10.5), x + 30, sy + 96);
        }

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(width * 1.5), (int)Math.Ceiling(height * 1.5), 144, 144, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }
}
