namespace AutoSkipper.Models;

using System.Collections.Generic;

internal class ConfigData
{
    private const int MinColorTolerance = 0;
    private const int MaxColorTolerance = 255;
    private const double MinDelay = 0.01;
    private const double MaxDelay = 5.0;
    private const string DefaultWindowTitle = "Genshin Impact";

    public string WindowTitle { get; set; } = DefaultWindowTitle;
    public double StandardDelayMin { get; set; } = 0.13;
    public double StandardDelayMax { get; set; } = 0.17;
    public int ColorTolerance { get; set; } = 10;
    public double LeftClickChance { get; set; } = 0.1;
    public List<Macro> Macros { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WindowTitle)) WindowTitle = DefaultWindowTitle;
        StandardDelayMin = Math.Clamp(StandardDelayMin, MinDelay, MaxDelay);
        StandardDelayMax = Math.Clamp(StandardDelayMax, MinDelay, MaxDelay);
        if (StandardDelayMin > StandardDelayMax) (StandardDelayMin, StandardDelayMax) = (StandardDelayMax, StandardDelayMin);
        ColorTolerance = Math.Clamp(ColorTolerance, MinColorTolerance, MaxColorTolerance);
        LeftClickChance = Math.Clamp(LeftClickChance, 0.0, 1.0);
    }
}
