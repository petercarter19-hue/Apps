namespace Spotlight;

/// <summary>
/// The light's animation: where the cut-out is and how dark the dimming is, over time.
/// Time is passed in (milliseconds), so tests can step it without a clock or a screen.
/// </summary>
internal sealed class SpotlightModel
{
    public const int GlideMs = 240, FadeInMs = 300, FadeOutMs = 220;

    public Box? Rect { get; private set; }
    public bool Maximized { get; private set; }
    /// <summary>0 = no dimming, 1 = full dimming at the chosen strength.</summary>
    public double Opacity { get; private set; }

    private Box _glideFrom, _glideTo;
    private long _glideStart;
    private bool _gliding;

    private double _opFrom, _opTo;
    private long _opStart;
    private int _opDur;
    private bool _fading;

    public bool Animating => _gliding || _fading;

    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double u = 1 - t;
        return 1 - u * u * u; // ease-out cubic: quick start, soft landing
    }

    public void Apply(Decision d, long now)
    {
        switch (d.Kind)
        {
            case DecisionKind.Show:
                Maximized = d.Maximized;
                if (Rect == null)
                {
                    Rect = d.Frame; _gliding = false;
                }
                else if (_gliding)
                {
                    _glideTo = d.Frame; // the target moved while gliding: re-aim the glide in flight
                }
                else if (d.Animate && Distance(Rect.Value, d.Frame) > 4)
                {
                    _glideFrom = Rect.Value; _glideTo = d.Frame; _glideStart = now; _gliding = true;
                }
                else Rect = d.Frame;
                StartFade(1, FadeInMs, now);
                break;
            case DecisionKind.Clear:
                if (Opacity > 0 || _fading) StartFade(0, FadeOutMs, now);
                else { Rect = null; _gliding = false; }
                break;
        }
    }

    private static int Distance(Box a, Box b) =>
        Math.Abs(a.Left - b.Left) + Math.Abs(a.Top - b.Top) + Math.Abs(a.Right - b.Right) + Math.Abs(a.Bottom - b.Bottom);

    private void StartFade(double to, int ms, long now)
    {
        if (_fading && _opTo == to) return;
        if (!_fading && Opacity == to) return;
        _opFrom = Opacity; _opTo = to; _opStart = now; _opDur = ms; _fading = true;
    }

    /// <summary>Moves the animation to <paramref name="now"/>. Returns true while more frames are needed.</summary>
    public bool Advance(long now)
    {
        if (_gliding)
        {
            double t = (now - _glideStart) / (double)GlideMs;
            if (t >= 1) { Rect = _glideTo; _gliding = false; }
            else Rect = Box.Lerp(_glideFrom, _glideTo, Ease(t));
        }
        if (_fading)
        {
            double t = _opDur <= 0 ? 1 : (now - _opStart) / (double)_opDur;
            if (t >= 1)
            {
                Opacity = _opTo; _fading = false;
                if (Opacity <= 0) { Rect = null; _gliding = false; }
            }
            else Opacity = _opFrom + (_opTo - _opFrom) * Ease(t);
        }
        return Animating;
    }
}
