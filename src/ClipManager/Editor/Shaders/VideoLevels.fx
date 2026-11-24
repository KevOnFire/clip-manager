// Videobereich korrigieren: Videos speichern Farben meist im "begrenzten" Bereich 16–235.
// Zeigt der Windows-Player das ungestreckt an, sind Schwarz grau und Weiß gedämpft (Bild wirkt blass).
// Hier wird 16–235 auf 0–255 gestreckt. Wird im GitHub-Build mit fxc zu VideoLevels.ps übersetzt.
sampler2D input : register(s0);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 c = tex2D(input, uv);
    float3 rgb = saturate((c.rgb / max(c.a, 0.0001) - 16.0 / 255.0) * (255.0 / 219.0));
    return float4(rgb * c.a, c.a);
}
