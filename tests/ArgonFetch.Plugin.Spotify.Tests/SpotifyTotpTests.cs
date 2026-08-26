using ArgonFetch.Plugin.Spotify;

namespace ArgonFetch.Plugin.Spotify.Tests
{
    public class SpotifyTotpTests
    {
        // Shaped like a real secret without being one; the live one rotates.
        private static readonly int[] Secret =
            [12, 56, 76, 33, 88, 44, 88, 33, 78, 78, 11, 66, 22, 22, 55, 69, 54];

        [Theory]
        [InlineData(1700000000L, "863172")]
        [InlineData(1735689600L, "318982")]
        public void GenerateTotp_MatchesTheWebPlayersAlgorithm(long unixSeconds, string expected)
        {
            Assert.Equal(expected, SpotifyWebPlayerClient.GenerateTotp(Secret, unixSeconds));
        }

        [Fact]
        public void GenerateTotp_HoldsForThirtySecondsAtATime()
        {
            // 1700000010 is itself a window boundary, so its window runs to ...039.
            var atStart = SpotifyWebPlayerClient.GenerateTotp(Secret, 1700000010);

            Assert.Equal(atStart, SpotifyWebPlayerClient.GenerateTotp(Secret, 1700000039));
            Assert.NotEqual(atStart, SpotifyWebPlayerClient.GenerateTotp(Secret, 1700000040));
        }

        [Fact]
        public void GenerateTotp_IsAlwaysSixDigits()
        {
            for (var offset = 0; offset < 40; offset++)
            {
                var code = SpotifyWebPlayerClient.GenerateTotp(Secret, 1700000000 + (offset * 30L));

                Assert.Equal(6, code.Length);
                Assert.All(code, character => Assert.True(char.IsAsciiDigit(character)));
            }
        }
    }
}
