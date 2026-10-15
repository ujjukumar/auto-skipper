using AutoSkipper.Models;
using AutoSkipper.Services;

namespace AutoSkipper.Tests;

public class DialogueDetectorTests : IDisposable
{
    // Game area at (100, 50), 1280x720: playing icon (56,30), choice pixels
    // (867, 538/526), loading pixel (800, 466) relative to the area.
    private const int AreaX = 100, AreaY = 50;
    private readonly MemoryDc _screen = new(1500, 900);
    private readonly TestLogger _logger = new();
    private readonly DialogueDetector _detector;

    public DialogueDetectorTests()
    {
        _detector = new DialogueDetector(new ConfigData(), _logger, _screen.Hdc);
        Assert.True(_detector.SetGameArea(AreaX, AreaY, 1280, 720));
    }

    public void Dispose() => _screen.Dispose();

    [Fact]
    public void NothingOnScreen_NoDialogue() => Assert.False(_detector.Detect());

    [Fact]
    public void PlayingIcon_AtGameAreaOffset_IsDialogue()
    {
        _screen.SetPixel(AreaX + 56, AreaY + 30, 236, 229, 216);
        Assert.True(_detector.Detect());
    }

    [Fact]
    public void PlayingIcon_WithoutOffset_IsIgnored()
    {
        _screen.SetPixel(56, 30, 236, 229, 216);
        Assert.False(_detector.Detect());
    }

    [Theory]
    [InlineData(538)]
    [InlineData(526)]
    public void ChoicePixel_IsDialogue(int y)
    {
        _screen.SetPixel(AreaX + 867, AreaY + y, 255, 255, 255);
        Assert.True(_detector.Detect());
    }

    [Fact]
    public void LoadingScreen_SuppressesChoiceDetection()
    {
        _screen.SetPixel(AreaX + 867, AreaY + 538, 255, 255, 255);
        _screen.SetPixel(AreaX + 800, AreaY + 466, 255, 255, 255);
        Assert.False(_detector.Detect());
    }

    [Fact]
    public void ColorsMatch_RespectsTolerance_AndRejectsFailedReads()
    {
        Assert.True(_detector.ColorsMatch(0x00FFFFFF, 255, 255, 255));
        Assert.True(_detector.ColorsMatch(0x00F5F5F5, 255, 255, 255));   // within default tolerance 10
        Assert.False(_detector.ColorsMatch(0x00E0E0E0, 255, 255, 255));
        // GetPixel's CLR_INVALID must not look like white.
        Assert.False(_detector.ColorsMatch(0xFFFFFFFF, 255, 255, 255));
    }

    [Fact]
    public void SetGameArea_RecalculatesLayoutOnlyWhenChanged()
    {
        Assert.True(_detector.SetGameArea(AreaX, AreaY, 1280, 720));
        Assert.True(_detector.SetGameArea(0, 0, 1920, 1080));

        Assert.Equal(PixelLayout.For(1920, 1080), _detector.Layout);
        Assert.Equal(2, _logger.Messages.Count(m => m.StartsWith("Game area:")));
    }

    [Fact]
    public void SetGameArea_RejectsMinimisedOrTinyWindows()
    {
        Assert.False(_detector.SetGameArea(0, 0, 0, 0));
        Assert.False(_detector.SetGameArea(0, 0, 150, 150));
    }

    [Fact]
    public void UpdateGameArea_UsesTheWindowClientAreaInScreenCoordinates()
    {
        TestHelpers.RunSta(() =>
        {
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
            form.ClientSize = new Size(1280, 720);

            Assert.True(_detector.UpdateGameArea(form.Handle));

            var origin = form.PointToScreen(Point.Empty);
            Assert.Equal((origin.X, origin.Y, 1280, 720), _detector.GameArea);
        });
    }
}
