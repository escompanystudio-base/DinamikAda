using System.Windows.Media;
using WpfPoint = System.Windows.Point;

namespace DinamikAda.App.Views;

/// <summary>
/// Pre-allocated, zero-heap-allocation parametric MacBook Notch + Concave Ear contour builder
/// for 240Hz spring morphing between flush bezel notch and floating pill geometry.
/// </summary>
public sealed class NotchGeometryBuilder
{
    private const double SquircleFactor = 0.60;
    private const double EarBezierFactor = 0.62;
    private const double RimInset = 0.5;

    private readonly PathFigure _shellFigure;
    private readonly LineSegment _shellTopChord;
    private readonly BezierSegment _shellRightEar;
    private readonly LineSegment _shellRightWall;
    private readonly BezierSegment _shellBottomRight;
    private readonly LineSegment _shellBottomEdge;
    private readonly BezierSegment _shellBottomLeft;
    private readonly LineSegment _shellLeftWall;
    private readonly BezierSegment _shellLeftEar;

    private readonly PathFigure _rimFigure;
    private readonly BezierSegment _rimRightEar;
    private readonly LineSegment _rimRightWall;
    private readonly BezierSegment _rimBottomRight;
    private readonly LineSegment _rimBottomEdge;
    private readonly BezierSegment _rimBottomLeft;
    private readonly LineSegment _rimLeftWall;
    private readonly BezierSegment _rimLeftEar;

    private bool _hasValue;
    private double _lastCenterX;
    private double _lastTopY;
    private double _lastWidth;
    private double _lastHeight;
    private double _lastEarRadius;
    private double _lastTopCornerRadius;
    private double _lastCornerRadius;

    public PathGeometry ShellGeometry { get; }
    public PathGeometry RimGeometry { get; }

    public NotchGeometryBuilder()
    {
        _shellTopChord = new LineSegment();
        _shellRightEar = new BezierSegment();
        _shellRightWall = new LineSegment();
        _shellBottomRight = new BezierSegment();
        _shellBottomEdge = new LineSegment();
        _shellBottomLeft = new BezierSegment();
        _shellLeftWall = new LineSegment();
        _shellLeftEar = new BezierSegment();

        _shellFigure = new PathFigure
        {
            IsClosed = true,
            IsFilled = true,
            Segments =
            [
                _shellTopChord,
                _shellRightEar,
                _shellRightWall,
                _shellBottomRight,
                _shellBottomEdge,
                _shellBottomLeft,
                _shellLeftWall,
                _shellLeftEar
            ]
        };

        ShellGeometry = new PathGeometry { Figures = [_shellFigure] };

        _rimRightEar = new BezierSegment();
        _rimRightWall = new LineSegment();
        _rimBottomRight = new BezierSegment();
        _rimBottomEdge = new LineSegment();
        _rimBottomLeft = new BezierSegment();
        _rimLeftWall = new LineSegment();
        _rimLeftEar = new BezierSegment();

        _rimFigure = new PathFigure
        {
            IsClosed = false,
            IsFilled = false,
            Segments =
            [
                _rimRightEar,
                _rimRightWall,
                _rimBottomRight,
                _rimBottomEdge,
                _rimBottomLeft,
                _rimLeftWall,
                _rimLeftEar
            ]
        };

        RimGeometry = new PathGeometry { Figures = [_rimFigure] };
    }

    /// <summary>
    /// Updates both <see cref="ShellGeometry"/> and <see cref="RimGeometry"/> in place without allocating.
    /// Returns <see langword="true"/> when at least one geometric input changed beyond 0.005 DIP.
    /// </summary>
    public bool Update(
        double centerX,
        double topY,
        double width,
        double height,
        double earRadius,
        double topCornerRadius,
        double cornerRadius)
    {
        if (_hasValue &&
            Math.Abs(_lastCenterX - centerX) <= 0.005 &&
            Math.Abs(_lastTopY - topY) <= 0.005 &&
            Math.Abs(_lastWidth - width) <= 0.005 &&
            Math.Abs(_lastHeight - height) <= 0.005 &&
            Math.Abs(_lastEarRadius - earRadius) <= 0.005 &&
            Math.Abs(_lastTopCornerRadius - topCornerRadius) <= 0.005 &&
            Math.Abs(_lastCornerRadius - cornerRadius) <= 0.005)
        {
            return false;
        }

        _hasValue = true;
        _lastCenterX = centerX;
        _lastTopY = topY;
        _lastWidth = width;
        _lastHeight = height;
        _lastEarRadius = earRadius;
        _lastTopCornerRadius = topCornerRadius;
        _lastCornerRadius = cornerRadius;

        double w = Math.Max(0.0, width);
        double h = Math.Max(0.0, height);
        if (w < 2.0 || h < 2.0)
        {
            var zero = new WpfPoint(centerX, topY);
            _shellFigure.StartPoint = zero;
            _shellTopChord.Point = zero;
            _shellRightEar.Point1 = zero;
            _shellRightEar.Point2 = zero;
            _shellRightEar.Point3 = zero;
            _shellRightWall.Point = zero;
            _shellBottomRight.Point1 = zero;
            _shellBottomRight.Point2 = zero;
            _shellBottomRight.Point3 = zero;
            _shellBottomEdge.Point = zero;
            _shellBottomLeft.Point1 = zero;
            _shellBottomLeft.Point2 = zero;
            _shellBottomLeft.Point3 = zero;
            _shellLeftWall.Point = zero;
            _shellLeftEar.Point1 = zero;
            _shellLeftEar.Point2 = zero;
            _shellLeftEar.Point3 = zero;

            _rimFigure.IsClosed = false;
            _rimFigure.StartPoint = zero;
            _rimRightEar.Point1 = zero;
            _rimRightEar.Point2 = zero;
            _rimRightEar.Point3 = zero;
            _rimRightWall.Point = zero;
            _rimBottomRight.Point1 = zero;
            _rimBottomRight.Point2 = zero;
            _rimBottomRight.Point3 = zero;
            _rimBottomEdge.Point = zero;
            _rimBottomLeft.Point1 = zero;
            _rimBottomLeft.Point2 = zero;
            _rimBottomLeft.Point3 = zero;
            _rimLeftWall.Point = zero;
            _rimLeftEar.Point1 = zero;
            _rimLeftEar.Point2 = zero;
            _rimLeftEar.Point3 = zero;
            return true;
        }

        double xL = centerX - w * 0.5;
        double xR = centerX + w * 0.5;
        double y0 = topY;
        double yB = topY + h;

        double rB = Math.Clamp(cornerRadius, 0.0, Math.Min(h * 0.5, w * 0.45));
        double rT = Math.Clamp(topCornerRadius, 0.0, Math.Min(h * 0.5, w * 0.45));
        double eClip = Math.Clamp((h - 8.0) / 10.0, 0.0, 1.0);
        double earW = Math.Max(0.0, earRadius) * eClip;
        double earH = Math.Min(earW, Math.Max(0.0, h - rB - 1.0));

        double dxTop = earW - rT;
        double dyTop = earH + rT;
        if (dyTop + rB > h)
        {
            double scaleDown = h / Math.Max(0.001, dyTop + rB);
            rT *= scaleDown;
            earH *= scaleDown;
            rB *= scaleDown;
            dxTop = earW - rT;
            dyTop = earH + rT;
        }

        double topCpOffsetX = earW * (1.0 - EarBezierFactor) - rT * (1.0 - SquircleFactor);
        double topCpOffsetY = earH * (1.0 - EarBezierFactor) + rT * (1.0 - SquircleFactor);
        double botCpOffset = rB * (1.0 - SquircleFactor);

        // Outer Shell Figure (clockwise from top-left chord start)
        var shellStart = new WpfPoint(xL - dxTop, y0);
        _shellFigure.StartPoint = shellStart;
        _shellTopChord.Point = new WpfPoint(xR + dxTop, y0);

        _shellRightEar.Point1 = new WpfPoint(xR + topCpOffsetX, y0);
        _shellRightEar.Point2 = new WpfPoint(xR, y0 + topCpOffsetY);
        _shellRightEar.Point3 = new WpfPoint(xR, y0 + dyTop);

        _shellRightWall.Point = new WpfPoint(xR, yB - rB);

        _shellBottomRight.Point1 = new WpfPoint(xR, yB - botCpOffset);
        _shellBottomRight.Point2 = new WpfPoint(xR - botCpOffset, yB);
        _shellBottomRight.Point3 = new WpfPoint(xR - rB, yB);

        _shellBottomEdge.Point = new WpfPoint(xL + rB, yB);

        _shellBottomLeft.Point1 = new WpfPoint(xL + botCpOffset, yB);
        _shellBottomLeft.Point2 = new WpfPoint(xL, yB - botCpOffset);
        _shellBottomLeft.Point3 = new WpfPoint(xL, yB - rB);

        _shellLeftWall.Point = new WpfPoint(xL, y0 + dyTop);

        _shellLeftEar.Point1 = new WpfPoint(xL, y0 + topCpOffsetY);
        _shellLeftEar.Point2 = new WpfPoint(xL - topCpOffsetX, y0);
        _shellLeftEar.Point3 = shellStart;

        // Inner Specular Rim Figure (0.5px inward inset; open top in NotchFlush, closed in FloatingTop/FreeFloat)
        double rxL = xL + RimInset;
        double rxR = xR - RimInset;
        double ry0 = y0 + RimInset;
        double ryB = Math.Max(ry0 + 0.5, yB - RimInset);
        double innerH = ryB - ry0;
        double innerW = Math.Max(1.0, rxR - rxL);

        double rBIn = Math.Clamp(rB - RimInset, 0.0, Math.Min(innerH * 0.5, innerW * 0.45));
        double rTIn = Math.Clamp(rT - RimInset, 0.0, Math.Min(innerH * 0.5, innerW * 0.45));
        double earHIn = Math.Min(earH, Math.Max(0.0, innerH - rBIn - rTIn));
        double dyTopIn = earHIn + rTIn;

        double rimStartRightX = xR + dxTop - RimInset;
        double rimEndLeftX = xL - dxTop + RimInset;
        if (rT > 0.25 && earW < 0.05)
        {
            // Pure floating capsule: match exact concentric inner radius horizontally as well.
            rimStartRightX = rxR - rTIn;
            rimEndLeftX = rxL + rTIn;
        }

        double rimTopCpOffsetX = earW * (1.0 - EarBezierFactor) - rTIn * (1.0 - SquircleFactor);
        double rimTopCpOffsetY = earHIn * (1.0 - EarBezierFactor) + rTIn * (1.0 - SquircleFactor);
        double rimBotCpOffset = rBIn * (1.0 - SquircleFactor);

        _rimFigure.IsClosed = rT > 0.25;
        _rimFigure.StartPoint = new WpfPoint(rimStartRightX, ry0);

        _rimRightEar.Point1 = new WpfPoint(rxR + rimTopCpOffsetX, ry0);
        _rimRightEar.Point2 = new WpfPoint(rxR, ry0 + rimTopCpOffsetY);
        _rimRightEar.Point3 = new WpfPoint(rxR, ry0 + dyTopIn);

        _rimRightWall.Point = new WpfPoint(rxR, ryB - rBIn);

        _rimBottomRight.Point1 = new WpfPoint(rxR, ryB - rimBotCpOffset);
        _rimBottomRight.Point2 = new WpfPoint(rxR - rimBotCpOffset, ryB);
        _rimBottomRight.Point3 = new WpfPoint(rxR - rBIn, ryB);

        _rimBottomEdge.Point = new WpfPoint(rxL + rBIn, ryB);

        _rimBottomLeft.Point1 = new WpfPoint(rxL + rimBotCpOffset, ryB);
        _rimBottomLeft.Point2 = new WpfPoint(rxL, ryB - rimBotCpOffset);
        _rimBottomLeft.Point3 = new WpfPoint(rxL, ryB - rBIn);

        _rimLeftWall.Point = new WpfPoint(rxL, ry0 + dyTopIn);

        _rimLeftEar.Point1 = new WpfPoint(rxL, ry0 + rimTopCpOffsetY);
        _rimLeftEar.Point2 = new WpfPoint(rxL - rimTopCpOffsetX, ry0);
        _rimLeftEar.Point3 = new WpfPoint(rimEndLeftX, ry0);

        return true;
    }

    /// <summary>
    /// Creates a frozen static notch or rim geometry for one-off UI previews such as SettingsWindow.
    /// </summary>
    public static PathGeometry CreateSnapshotGeometry(
        double width,
        double height,
        double earRadius,
        double topCornerRadius,
        double cornerRadius,
        bool isRim)
    {
        double centerX = width * 0.5 + Math.Max(0.0, earRadius);
        return CreateSnapshotGeometry(centerX, 0.0, width, height, earRadius, topCornerRadius, cornerRadius, isRim);
    }

    /// <summary>
    /// Creates a frozen static notch or rim geometry positioned at explicit canvas coordinates.
    /// </summary>
    public static PathGeometry CreateSnapshotGeometry(
        double centerX,
        double topY,
        double width,
        double height,
        double earRadius,
        double topCornerRadius,
        double cornerRadius,
        bool isRim)
    {
        var builder = new NotchGeometryBuilder();
        builder.Update(centerX, topY, width, height, earRadius, topCornerRadius, cornerRadius);
        var geo = isRim ? builder.RimGeometry : builder.ShellGeometry;
        geo.Freeze();
        return geo;
    }
}
