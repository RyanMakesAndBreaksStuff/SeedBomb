using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Seedbomb.Resources
{
    /// <summary>
    /// Splash screen for Seed Bomb for Microsoft Dataverse — WPF port of
    /// concept 07 (Indigo Dark · halo arcs + circling ring loader), 800 x 480.
    ///
    /// Loader contract:
    ///   • The circling arc is ALWAYS indeterminate ambiance. It never maps to,
    ///     mirrors or reflects the loading percentage — and it never changes
    ///     behaviour on completion either: it spins at its own constant rhythm
    ///     from Loaded until the window fades, whatever SetProgress reports.
    ///   • <see cref="SetProgress"/> only drives the lifecycle: values are a
    ///     monotonic 0..1 fraction of boot; reaching 1.0 parks the emerald ✓
    ///     badge at 12 o'clock and schedules the exit. The arc is untouched.
    ///   • Minimum-visible rule: the window never closes before 3 s have
    ///     elapsed since Loaded. Finish early → the completed state (ring +✓)
    ///     holds until the 3 s mark; finish late → the double takes over and
    ///     the exit follows it (plus a short ✓ hold).
    ///   • <see cref="SetStatus"/> shows a small console-style line inside the
    ///     seal ring. Both methods are dispatcher-safe: call them from any thread.
    ///
    /// The curved title / sub-line / slogan are true vector geometry: each glyph
    /// is measured with FormattedText, converted to Geometry and matrixed onto
    /// circles concentric with the bomb (centre 400,238). The arc Paths are the
    /// canvas's last children, so type is always the foreground layer.
    /// </summary>
    public partial class SplashWindow : Window
    {
        private const double MinVisibleMs = 3000.0;   // hard floor: splash stays up
        private const double DoneHoldMs = 450.0;      // ✓ breathes before the fade
        private const double FadeMs = 350.0;
        private const double SpinSeconds = 2.6;

        // arc radii + type sizes, identical to render_concepts.py concept 07
        private const double Cx = 400.0, Cy = 238.0;
        private const double RTitle = 172.0, RSub = 152.0, RSlogan = 124.0;

        private DateTime _shownAt;
        private double _progress = -1.0;
        private bool _loaded, _done, _closing, _pendingDone;

        public SplashWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            TitleArc.Data = BuildArcGeometry("SEED BOMB",
                new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                25, 3, RTitle, top: true);
            SubArc.Data = BuildArcGeometry("FOR MICROSOFT DATAVERSE",
                new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                10, 3, RSub, top: true);
            SloganArc.Data = BuildArcGeometry("Mock Data That Goes Boom! Before You Do",
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                13, 0.5, RSlogan, top: false);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _shownAt = DateTime.UtcNow;
            _loaded = true;
            StartSpin();
            if (_pendingDone) Complete();
        }

        /// <summary>Indeterminate circling arc. Pure ambiance — never tied to %.</summary>
        private void StartSpin()
        {
            var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(SpinSeconds))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTargetName(anim, "LoaderRot");
            Storyboard.SetTargetProperty(anim, new PropertyPath("Angle"));
            var spin = new Storyboard();
            spin.Children.Add(anim);
            spin.Begin(this);
        }

        /// <summary>
        /// Reports boot progress as a fraction 0..1 (monotonic; backwards values
        /// are ignored). Nothing visual is bound to the value — the arc keeps
        /// spinning at its own rhythm regardless. Reaching 1.0 means "loading
        /// done": the ✓ badge parks at 12 o'clock and the window exits at
        /// max(3 s since shown, now + 450 ms). The arc is never altered.
        /// </summary>
        public void SetProgress(double progress)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetProgress(progress)));
                return;
            }
            if (_done || _closing) return;

            progress = Math.Min(1.0, Math.Max(0.0, progress));
            if (progress < _progress) return;      // progress bars don't walk backwards
            _progress = progress;

            if (progress >= 1.0)
            {
                if (_loaded) Complete();
                else _pendingDone = true;          // finished before Loaded: honour the floor anyway
            }
        }

        /// <summary>Small status line inside the seal ring, e.g. "Inspecting schema…".</summary>
        public void SetStatus(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetStatus(message)));
                return;
            }
            StatusText.Text = message ?? string.Empty;
        }

        private void Complete()
        {
            _done = true;
            // NOTE: the spin is deliberately left running — the arc light never
            // reacts to progress, not even at 100 %. Only the ✓ badge and the
            // exit timer respond to completion.
            DoneBadge.Visibility = Visibility.Visible;

            double elapsedMs = (DateTime.UtcNow - _shownAt).TotalMilliseconds;
            double delayMs = Math.Max(MinVisibleMs - elapsedMs, DoneHoldMs);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                FadeAndClose();
            };
            timer.Start();
        }

        private void FadeAndClose()
        {
            if (_closing) return;
            _closing = true;

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeMs));
            Storyboard.SetTarget(fade, Root);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
            var sb = new Storyboard();
            sb.Children.Add(fade);
            sb.Completed += (_, _) => Close();
            sb.Begin(this);
        }

        /// <summary>
        /// Builds one GeometryGroup whose glyphs sit baselined on a circle of
        /// radius <paramref name="r"/> centred on the bomb. top:true  = rainbow
        /// arc above the bomb (ascenders outward); top:false = mirrored smile
        /// arc below it (ascenders toward the centre).
        /// </summary>
        internal static Geometry BuildArcGeometry(string text, Typeface typeface, double emSize,
            double letterSpacing, double r, bool top)
        {
            var group = new GeometryGroup();
            double[] widths = new double[text.Length];
            double total = 0;
            for (int i = 0; i < text.Length; i++)
            {
                widths[i] = Measure(text[i], typeface, emSize);
                total += widths[i] + letterSpacing;
            }
            total -= letterSpacing;

            double theta = -total / (2 * r);
            for (int i = 0; i < text.Length; i++)
            {
                double adv = widths[i] + letterSpacing;
                double mid = theta + adv / (2 * r);

                double px = Cx + r * Math.Sin(mid);
                double py = top ? Cy - r * Math.Cos(mid) : Cy + r * Math.Cos(mid);
                double deg = top ? mid * 180 / Math.PI : -mid * 180 / Math.PI;

                // glyph geometry with baseline-left at the origin, then centre it
                // on its advance width, rotate to the tangent, translate onto the arc
                Geometry g = new FormattedText(text[i].ToString(), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, emSize, Brushes.Black, 1.0)
                    .BuildGeometry(new Point(-widths[i] / 2, 0))
                    .Clone();

                var m = new Matrix();
                m.Rotate(deg);
                m.OffsetX += px;
                m.OffsetY += py;
                g.Transform = new MatrixTransform(m);
                group.Children.Add(g);

                theta += adv / r;
            }
            return group;
        }

        private static double Measure(char ch, Typeface typeface, double emSize)
        {
            return new FormattedText(ch.ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, emSize, Brushes.Black, 1.0).Width;
        }
    }
}
