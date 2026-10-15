namespace AutoSkipper.Models;

// Positions of the pixels used for dialogue detection, relative to the game's
// client area. Base coordinates are for 1920x1080; other sizes are scaled, with
// special handling for non-16:9 (e.g. ultrawide) layouts where the HUD shifts.
internal readonly record struct PixelLayout(
    (int x, int y) PlayingIcon,
    (int x, int ly, int hy) DialogueIcon,
    (int x, int y) LoadingPixel)
{
    private const int BaseWidth = 1920;
    private const int BaseHeight = 1080;
    private const int PlayingIconX = 84;
    private const int PlayingIconY = 46;
    private const int DialogueIconX = 1301;
    private const int DialogueIconLowY = 808;
    private const int DialogueIconHighY = 790;
    private const int DialogueIconWsLowY = 810;
    private const int DialogueIconWsHighY = 792;
    private const int LoadingPixelX = 1200;
    private const int LoadingPixelY = 700;
    private const int PlayingIconWsX = 230;
    private const int DialogueIconWsX = 2770;
    private const float DialogueIconWsExtra = 0.02f;

    public static PixelLayout For(int width, int height)
    {
        int Wa(int x) => (int)(x / (float)BaseWidth * width);
        int Ha(int y) => (int)(y / (float)BaseHeight * height);
        int ScalePos(int hd, int doubleHd, float extra = 0.0f)
        {
            if (width <= 3840) extra = 0.0f;
            int diff = doubleHd - hd;
            return (int)(hd + (width - BaseWidth) * ((diff / (float)BaseWidth) + extra));
        }

        bool ws = width > BaseWidth && Math.Abs((double)height / width - 0.5625) > 0.001;

        int playingX = ws ? Math.Min(ScalePos(PlayingIconX, PlayingIconWsX), PlayingIconWsX) : Wa(PlayingIconX);
        int dialogueX = ws ? ScalePos(DialogueIconX, DialogueIconWsX, DialogueIconWsExtra) : Wa(DialogueIconX);
        int dialogueLowY = ws ? Ha(DialogueIconWsLowY) : Ha(DialogueIconLowY);
        int dialogueHighY = ws ? Ha(DialogueIconWsHighY) : Ha(DialogueIconHighY);

        return new PixelLayout(
            (playingX, Ha(PlayingIconY)),
            (dialogueX, dialogueLowY, dialogueHighY),
            (Wa(LoadingPixelX), Ha(LoadingPixelY)));
    }
}
