using ArgonFetch.Plugin.TikTok;

namespace ArgonFetch.Plugin.TikTok.Tests
{
    public class TikTokTextTests
    {
        [Theory]
        [InlineData("<b>Hello</b>", "Hello")]
        [InlineData("Caption &amp; more", "Caption & more")]
        [InlineData("  spread   out  ", "spread out")]
        // The page runs the button's own label straight into the title.
        [InlineData("Real Title Download without Watermark", "Real Title")]
        [InlineData(null, "")]
        [InlineData("   ", "")]
        public void Clean_ReducesPageTextToWhatWasActuallyWritten(string? input, string expected) =>
            Assert.Equal(expected, TikTokProvider.Clean(input));

        [Theory]
        [InlineData("https://www.tiktok.com/@someone/video/123", true)]
        [InlineData("https://vm.tiktok.com/ABC123/", true)]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false)]
        // Nobody else's domain, however it is dressed up.
        [InlineData("https://tiktok.com.evil.example/x", false)]
        public void UrlPatterns_ClaimTikTokAndNothingElse(string url, bool expected)
        {
            var pattern = new System.Text.RegularExpressions.Regex(
                new TikTokProvider().UrlPatterns[0],
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            Assert.Equal(expected, pattern.IsMatch(url));
        }
    }
}
