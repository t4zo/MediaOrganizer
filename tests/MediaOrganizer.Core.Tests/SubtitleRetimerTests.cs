using System.Text;
using MediaOrganizer.Core.Subtitles;

namespace MediaOrganizer.Core.Tests;

public sealed class SubtitleRetimerTests
{
    private static string Retime(string text, double offsetSeconds, double scale = 1) =>
        SubtitleRetimer.Apply(text, new SubtitleAdjustment(TimeSpan.FromSeconds(offsetSeconds), scale));

    [Fact]
    public void SrtIsShiftedKeepingTextAndLineEndings()
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Olá</i>, mundo\r\n\r\n2\r\n00:00:03,250 --> 00:00:04,000\r\n00:00:09 não é tempo\r\n";

        var result = Retime(srt, 0.4);

        Assert.Equal("1\r\n00:00:01,400 --> 00:00:02,900\r\n<i>Olá</i>, mundo\r\n\r\n2\r\n00:00:03,650 --> 00:00:04,400\r\n00:00:09 não é tempo\r\n", result);
    }

    [Fact]
    public void TimesThatWouldBeNegativeStopAtZero()
    {
        var result = Retime("1\n00:00:00,300 --> 00:00:01,000\nCedo\n", -0.5);

        Assert.Equal("1\n00:00:00,000 --> 00:00:00,500\nCedo\n", result);
    }

    [Fact]
    public void ScaleStretchesFromTheStartAndCarriesIntoHours()
    {
        // 59:00 × 25 / 23,976 = 1:01:31,191; menos 1 s de deslocamento.
        var result = Retime("1\n00:59:00,000 --> 00:59:02,000\nTexto\n", -1, 25 / 23.976);

        Assert.Equal("1\n01:01:30,191 --> 01:01:32,277\nTexto\n", result);
    }

    [Fact]
    public void HoursAboveNineAreWritten()
    {
        var result = Retime("1\n09:59:59,900 --> 10:00:01,000\nLongo\n", 0.2);

        Assert.Equal("1\n10:00:00,100 --> 10:00:01,200\nLongo\n", result);
    }

    [Fact]
    public void VttKeepsDotsCueSettingsAndShortTimes()
    {
        const string vtt = "WEBVTT\n\n00:01.000 --> 00:02.000 align:start position:10%\nOi\n\n00:59:59.500 --> 01:00:00.500\nTchau\n";

        var result = Retime(vtt, 1.25);

        Assert.Equal("WEBVTT\n\n00:02.250 --> 00:03.250 align:start position:10%\nOi\n\n01:00:00.750 --> 01:00:01.750\nTchau\n", result);
    }

    [Fact]
    public void VttWithoutHoursGainsHoursPastOneHour()
    {
        var result = Retime("WEBVTT\n\n59:59.000 --> 59:59.900\nOi\n", 2);

        Assert.Equal("WEBVTT\n\n01:00:01.000 --> 01:00:01.900\nOi\n", result);
    }

    [Fact]
    public void AssRetimesDialogueAndCommentsKeepingStylesAndCommas()
    {
        const string ass = """
            [Script Info]
            Title: Teste
            PlayResX: 1920

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour
            Style: Default,Arial,48,&H00FFFFFF

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\an8}Olá, mundo, tudo bem?
            Comment: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,nota
            Dialogue: 0,9:59:59.95,10:00:00.00,Default,,0,0,0,,Fim
            """;

        var result = Retime(ass, 0.355);

        Assert.Equal(ass
            .Replace("0:00:01.00,0:00:02.50", "0:00:01.36,0:00:02.86", StringComparison.Ordinal)
            .Replace("0:00:03.00,0:00:04.00", "0:00:03.36,0:00:04.36", StringComparison.Ordinal)
            .Replace("9:59:59.95,10:00:00.00", "10:00:00.31,10:00:00.36", StringComparison.Ordinal), result);
    }

    [Fact]
    public void AssUsesTheEventsFormatColumns()
    {
        const string ssa = "[Events]\nFormat: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
                           "Dialogue: Marked=0,0:00:00.20,0:00:01.00,Default,,0000,0000,0000,,Oi\n";

        var result = Retime(ssa, -0.5);

        Assert.EndsWith("Dialogue: Marked=0,0:00:00.00,0:00:00.50,Default,,0000,0000,0000,,Oi\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BytesComeBackAsUtf8WithBom()
    {
        var content = SubtitleText.ToUtf8(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nAção\n"));

        var bytes = SubtitleRetimer.Apply(content, new SubtitleAdjustment(TimeSpan.FromSeconds(1)));

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("1\n00:00:02,000 --> 00:00:03,000\nAção\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Theory]
    [InlineData(SyncVerdict.Shifted, 2.0, 1.0, true, true)]
    [InlineData(SyncVerdict.FrameRate, 0.0, 1.0427, true, true)]
    [InlineData(SyncVerdict.Synced, -0.4, 1.0, true, false)]
    [InlineData(SyncVerdict.Synced, 0.1, 1.0, false, false)]
    [InlineData(SyncVerdict.Mismatch, 3.0, 1.0, false, false)]
    [InlineData(SyncVerdict.Hash, 0.0, 1.0, false, false)]
    public void CorrectionIsOfferedOnlyWhenItHelps(SyncVerdict verdict, double offset, double scale, bool offered, bool byDefault)
    {
        var sync = new SyncResult(0.5, TimeSpan.FromSeconds(offset), scale, verdict);

        Assert.Equal(offered, sync.Correction is not null);
        Assert.Equal(byDefault, sync.CorrectByDefault && sync.Correction is not null);
        if (offered) Assert.Equal(new SubtitleAdjustment(TimeSpan.FromSeconds(offset), scale), sync.Correction);
    }
}
