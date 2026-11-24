using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClipManager.Editor;

/// <summary>
/// Streckt die Video-Farben von 16–235 auf 0–255 (siehe Shaders/VideoLevels.fx), damit das Bild im
/// Player so kräftig aussieht wie in anderen Playern.
/// </summary>
public sealed class VideoLevelsEffect : ShaderEffect
{
    private static readonly Uri ShaderUri = new("pack://application:,,,/Editor/Shaders/VideoLevels.ps");

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(VideoLevelsEffect), 0);

    private VideoLevelsEffect()
    {
        PixelShader = new PixelShader { UriSource = ShaderUri };
        UpdateShaderValue(InputProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>Null, wenn der Shader fehlt (z.B. lokal gebaut ohne fxc) – dann ohne Korrektur.</summary>
    public static VideoLevelsEffect? TryCreate()
    {
        try
        {
            if (Application.GetResourceStream(ShaderUri) == null)
            {
                return null;
            }

            return new VideoLevelsEffect();
        }
        catch (Exception ex)
        {
            Core.Log.Error("Farbkorrektur für die Vorschau nicht verfügbar", ex);
            return null;
        }
    }
}
