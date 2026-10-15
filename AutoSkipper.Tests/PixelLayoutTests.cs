using AutoSkipper.Models;

namespace AutoSkipper.Tests;

public class PixelLayoutTests
{
    // Expected values were recorded from the original ScreenConfig implementation
    // before the layout math was moved, so any change here is a behaviour change.
    [Theory]
    [InlineData(1280, 720, 56, 30, 867, 538, 526, 800, 466)]
    [InlineData(1366, 768, 59, 32, 925, 574, 561, 853, 497)]
    [InlineData(1600, 900, 70, 38, 1084, 673, 658, 1000, 583)]
    [InlineData(1920, 1080, 84, 46, 1301, 808, 790, 1200, 699)]
    [InlineData(1920, 1200, 84, 51, 1301, 897, 877, 1200, 777)]
    [InlineData(2560, 1080, 132, 46, 1790, 810, 792, 1600, 699)]
    [InlineData(2560, 1440, 112, 61, 1734, 1077, 1053, 1600, 933)]
    [InlineData(3440, 1440, 199, 61, 2463, 1080, 1056, 2150, 933)]
    [InlineData(3840, 1600, 230, 68, 2770, 1200, 1173, 2400, 1037)]
    [InlineData(3840, 2160, 168, 92, 2602, 1616, 1580, 2400, 1399)]
    [InlineData(5120, 1440, 230, 61, 3813, 1080, 1056, 3200, 933)]
    [InlineData(5120, 2160, 230, 92, 3813, 1620, 1584, 3200, 1399)]
    [InlineData(1024, 768, 44, 32, 693, 574, 561, 640, 497)]
    public void For_MatchesOriginalScaling(int width, int height,
        int playX, int playY, int dlgX, int dlgLowY, int dlgHighY, int loadX, int loadY)
    {
        var layout = PixelLayout.For(width, height);

        Assert.Equal((playX, playY), layout.PlayingIcon);
        Assert.Equal((dlgX, dlgLowY, dlgHighY), layout.DialogueIcon);
        Assert.Equal((loadX, loadY), layout.LoadingPixel);
    }
}
