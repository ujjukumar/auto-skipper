using AutoSkipper.Services;
using AutoSkipper.UI;
using static AutoSkipper.Tests.TestHelpers;

namespace AutoSkipper.Tests;

// The logger is static and shared, so these tests log unique markers and look
// only for those.
public class LogViewTests
{
    private static readonly Logger Log = new();

    private static bool HasViewers() =>
        typeof(Logger).GetField("OnLogMessage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null) != null;

    private static void WithLogView(Action<Form, LogView, RichTextBox> test) => RunSta(() =>
    {
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(900, 580), ShowInTaskbar = false };
        var view = new LogView();
        form.Controls.Add(view);
        test(form, view, Field<RichTextBox>(view, "_richTextBox"));
    });

    [Fact]
    public void OnlyListensWhileOnScreen()
    {
        WithLogView((form, view, _) =>
        {
            Assert.False(HasViewers());
            form.Show();
            Assert.True(HasViewers());
            view.Visible = false; // e.g. Macros tab selected
            Assert.False(HasViewers());
            view.Visible = true;
            form.Hide();
            Assert.False(HasViewers());
        });
    }

    [Fact]
    public void ManyLines_AreCappedAndKeepTextIntact()
    {
        WithLogView((form, _, rtb) =>
        {
            form.Show();
            string id = Guid.NewGuid().ToString("N")[..8];
            for (int i = 0; i < 3000; i++) Log.Log($"{id} msg {i} [x] \u2192 \u2713");
            Log.LogWarning($"{id} END");

            Assert.True(WaitUntil(() => rtb.Text.Contains($"{id} END"), 10000));
            int lines = rtb.GetLineFromCharIndex(rtb.TextLength);
            Assert.InRange(lines, 2000, 2202);
            Assert.DoesNotContain($"{id} msg 0 [x]", rtb.Text);
            Assert.Contains($"{id} msg 2999 [x] \u2192 \u2713", rtb.Text);
        });
    }

    [Fact]
    public void LinesAreColouredByPart()
    {
        WithLogView((form, _, rtb) =>
        {
            form.Show();
            string marker = "COLOR" + Guid.NewGuid().ToString("N")[..8];
            Log.Log("multi\r\nline");
            Log.LogWarning(marker);
            Assert.True(WaitUntil(() => rtb.Text.Contains(marker)));

            int at = rtb.Text.LastIndexOf(marker, StringComparison.Ordinal);
            int lineStart = rtb.GetFirstCharIndexFromLine(rtb.GetLineFromCharIndex(at));
            rtb.Select(lineStart, 8);
            Assert.Equal(Color.Gray.ToArgb(), rtb.SelectionColor.ToArgb());
            rtb.Select(lineStart + 10, 7);
            Assert.Equal(Color.FromArgb(206, 145, 120).ToArgb(), rtb.SelectionColor.ToArgb());
            rtb.Select(at, marker.Length);
            Assert.Equal(Theme.ForegroundColor.ToArgb(), rtb.SelectionColor.ToArgb());
        });
    }

    [Fact]
    public void Reopening_ReplaysHistory_ExceptClearedLines()
    {
        WithLogView((form, view, rtb) =>
        {
            form.Show();
            string hidden = "HIDDEN" + Guid.NewGuid().ToString("N")[..8];
            form.Hide();
            Log.Log(hidden);
            Thread.Sleep(200);

            form.Show();
            Assert.True(WaitUntil(() => rtb.Text.Contains(hidden)));

            Invoke(view, "ClearLogs");
            string after = "AFTER" + Guid.NewGuid().ToString("N")[..8];
            Log.Log(after);
            Assert.True(WaitUntil(() => rtb.Text.Contains(after)));
            form.Hide();
            form.Show();

            Assert.Contains(after, rtb.Text);
            Assert.DoesNotContain(hidden, rtb.Text);
        });
    }
}
