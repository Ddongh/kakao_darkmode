using KakaoDark.Native;

namespace KakaoDark.Core;

public enum ColorPreset
{
    SimpleInvert,
    SmartInvert,
    SoftDark
}

public static class ColorPresets
{
    public static MAGCOLOREFFECT IdentityMatrix() => Build(new float[]
    {
        1, 0, 0, 0, 0,
        0, 1, 0, 0, 0,
        0, 0, 1, 0, 0,
        0, 0, 0, 1, 0,
        0, 0, 0, 0, 1
    });

    public static MAGCOLOREFFECT SimpleInvert() => Build(new float[]
    {
        -1,  0,  0, 0, 0,
         0, -1,  0, 0, 0,
         0,  0, -1, 0, 0,
         0,  0,  0, 1, 0,
         1,  1,  1, 0, 1
    });

    public static MAGCOLOREFFECT SmartInvert() => Build(new float[]
    {
         0.333f, -0.667f, -0.667f, 0, 0,
        -0.667f,  0.333f, -0.667f, 0, 0,
        -0.667f, -0.667f,  0.333f, 0, 0,
         0,       0,       0,      1, 0,
         1,       1,       1,      0, 1
    });

    public static MAGCOLOREFFECT SoftDark() => Build(new float[]
    {
         0.85f,  0,     0,    0, 0,
         0,      0.85f, 0,    0, 0,
         0,      0,     0.85f,0, 0,
         0,      0,     0,    1, 0,
        -0.6f,  -0.6f, -0.6f, 0, 1
    });

    public static MAGCOLOREFFECT FromPreset(ColorPreset preset) => preset switch
    {
        ColorPreset.SimpleInvert => SimpleInvert(),
        ColorPreset.SmartInvert  => SmartInvert(),
        ColorPreset.SoftDark     => SoftDark(),
        _ => IdentityMatrix()
    };

    public static string Label(ColorPreset preset) => preset switch
    {
        ColorPreset.SimpleInvert => "단순 반전",
        ColorPreset.SmartInvert  => "스마트 인버전",
        ColorPreset.SoftDark     => "부드러운 다크",
        _ => preset.ToString()
    };

    private static unsafe MAGCOLOREFFECT Build(float[] values)
    {
        if (values.Length != 25)
            throw new ArgumentException("Color matrix must have 25 floats (5x5).", nameof(values));

        var effect = new MAGCOLOREFFECT();
        for (int i = 0; i < 25; i++)
            effect.transform[i] = values[i];
        return effect;
    }
}
