using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClipManager;

/// <summary>
/// Startbildschirm: erscheint mittig, solange Einstellungen, FFmpeg und Clips geladen werden.
/// Logo ploppt rein, Name und Infos gleiten nach, der Ladebalken zeigt den aktuellen Schritt.
/// </summary>
public partial class SplashWindow : Window
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    public SplashWindow()
    {
        InitializeComponent();
        VersionRun.Text = AppUpdater.CurrentVersionText;

        // Inhalt an den runden Ecken abschneiden (das große Hintergrund-Logo ragt sonst raus).
        Inner.SizeChanged += (_, e) => Inner.Clip = new RectangleGeometry(new Rect(e.NewSize), 15, 15);
        Loaded += (_, _) => PlayIntro();
    }

    /// <summary>Wie lange die Einblend-Animation dauert (so lange bleibt der Bildschirm mindestens).</summary>
    public static TimeSpan IntroDuration { get; } = TimeSpan.FromSeconds(1.3);

    /// <summary>Zeigt den nächsten Ladeschritt (Text und Fortschritt 0–100).</summary>
    public void SetStep(string text, double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        StepText.Text = text;
        PercentText.Text = $"{percent:0} %";
        Animate(BarScale, ScaleTransform.ScaleXProperty, percent / 100, 0.35, 0);
    }

    /// <summary>Blendet den Startbildschirm aus (etwas größer werdend) und gibt Bescheid, wenn fertig.</summary>
    public Task FadeOutAsync()
    {
        var done = new TaskCompletionSource();
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.35)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => done.TrySetResult();
        Card.BeginAnimation(OpacityProperty, fade);
        Animate(CardScale, ScaleTransform.ScaleXProperty, 1.03, 0.35, 0);
        Animate(CardScale, ScaleTransform.ScaleYProperty, 1.03, 0.35, 0);
        return done.Task;
    }

    private void PlayIntro()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            // Animationen in Windows ausgeschaltet: alles gleich sichtbar.
            foreach (var element in new UIElement[] { Card, Logo, NameText, TagText, Bottom })
            {
                element.Opacity = 1;
            }

            CardScale.ScaleX = CardScale.ScaleY = LogoScale.ScaleX = LogoScale.ScaleY = 1;
            CardShift.Y = NameShift.Y = TagShift.Y = BottomShift.Y = LogoRotate.Angle = 0;
            return;
        }

        // Karte gleitet rein
        Animate(Card, OpacityProperty, 1, 0.5, 0);
        Animate(CardShift, TranslateTransform.YProperty, 0, 0.5, 0);
        Animate(CardScale, ScaleTransform.ScaleXProperty, 1, 0.5, 0);
        Animate(CardScale, ScaleTransform.ScaleYProperty, 1, 0.5, 0);

        // Logo ploppt rein (leicht über das Ziel hinaus und zurück)
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
        Animate(Logo, OpacityProperty, 1, 0.4, 0.15);
        Animate(LogoScale, ScaleTransform.ScaleXProperty, 1, 0.8, 0.15, pop);
        Animate(LogoScale, ScaleTransform.ScaleYProperty, 1, 0.8, 0.15, pop);
        Animate(LogoRotate, RotateTransform.AngleProperty, 0, 0.8, 0.15, pop);

        // Name, Untertitel und Ladebereich nacheinander
        Rise(NameText, NameShift, 0.45);
        Rise(TagText, TagShift, 0.6);
        Rise(Bottom, BottomShift, 0.75);

        // Hintergrund-Logo schwebt langsam hin und her
        var drift = TimeSpan.FromSeconds(4);
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
        BackgroundRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-8, -4, drift)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = sine,
        });
        BackgroundShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -8, drift)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = sine,
        });
    }

    private static void Rise(UIElement element, TranslateTransform shift, double delay)
    {
        Animate(element, OpacityProperty, 1, 0.5, delay);
        Animate(shift, TranslateTransform.YProperty, 0, 0.5, delay);
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double to, double seconds, double delay, IEasingFunction? easing = null)
    {
        target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = easing ?? EaseOut,
        });
    }
}
