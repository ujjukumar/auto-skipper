namespace AutoSkipper.Models;

internal class ConfigData
{
    private const int MinColorTolerance = 0;
    private const int MaxColorTolerance = 255;
    private const double MinDelay = 0.01;
    private const double MaxDelay = 5.0;

    public string WindowTitle { get; set; } = "Genshin Impact";
    public double StandardDelayMin { get; set; } = 0.13;
    public double StandardDelayMax { get; set; } = 0.17;
    public int ColorTolerance { get; set; } = 10;

    public void Validate()
    {
        StandardDelayMin = Math.Clamp(StandardDelayMin, MinDelay, MaxDelay);
        StandardDelayMax = Math.Clamp(StandardDelayMax, MinDelay, MaxDelay);
        if (StandardDelayMin > StandardDelayMax) (StandardDelayMin, StandardDelayMax) = (StandardDelayMax, StandardDelayMin);
        ColorTolerance = Math.Clamp(ColorTolerance, MinColorTolerance, MaxColorTolerance);
    }
}
