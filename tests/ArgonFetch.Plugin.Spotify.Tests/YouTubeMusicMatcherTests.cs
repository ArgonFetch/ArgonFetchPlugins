using ArgonFetch.Plugin.Spotify;

namespace ArgonFetch.Plugin.Spotify.Tests
{
    public class YouTubeMusicMatcherTests
    {
        private const long ThreeMinutesMs = 180_000;
        private const long ThreeMinutesSec = 180;

        private static MatchCandidate Candidate(
            string title,
            string? artist = "Rick Astley",
            long durationSec = ThreeMinutesSec,
            string details = "") => new(title, artist, durationSec, details);

        private static MatchCandidate? Match(
            IReadOnlyList<MatchCandidate> candidates,
            string title = "Never Gonna Give You Up",
            string artist = "Rick Astley",
            long durationMs = ThreeMinutesMs,
            bool officialShelf = false) =>
            YouTubeMusicMatcher.BestMatch(candidates, title, artist, durationMs, officialShelf);

        [Fact]
        public void BestMatch_TakesTheRecordingThatWasAskedFor()
        {
            var wanted = Candidate("Never Gonna Give You Up");

            Assert.Same(wanted, Match([wanted]));
        }

        [Fact]
        public void BestMatch_ReturnsNull_RatherThanTheWrongRecording()
        {
            var result = Match([Candidate("Together Forever"), Candidate("Whenever You Need Somebody")]);

            Assert.Null(result);
        }

        [Theory]
        [InlineData("Never Gonna Give You Up (Instrumental)")]
        [InlineData("Never Gonna Give You Up (Karaoke Version)")]
        [InlineData("Never Gonna Give You Up - Piano Cover")]
        [InlineData("Never Gonna Give You Up (Nightcore)")]
        [InlineData("Never Gonna Give You Up [Slowed + Reverb]")]
        public void BestMatch_RejectsReworksNobodyAskedFor(string title)
        {
            Assert.Null(Match([Candidate(title)]));
        }

        [Fact]
        public void BestMatch_KeepsTheReworkWhenTheRequestIsForOne()
        {
            var remix = Candidate("Sonne (Remix)", artist: "Rammstein");

            Assert.Same(remix, Match([remix], title: "Sonne (Remix)", artist: "Rammstein"));
        }

        [Fact]
        public void BestMatch_MatchesWhenTheRequestSpellsOutAFeatureCreditAndTheCandidateDoesNot()
        {
            // The guest is in the name here, in brackets there, and normalisation drops it.
            var candidate = Candidate("Stay", artist: "The Kid LAROI");

            Assert.Same(candidate, Match(
                [candidate],
                title: "Stay (feat. Justin Bieber)",
                artist: "The Kid LAROI"));
        }

        [Fact]
        public void BestMatch_RejectsAnUploadCreditedToSomebodyElse()
        {
            Assert.Null(Match([Candidate("Never Gonna Give You Up", artist: "Some Karaoke Channel")]));
        }

        [Fact]
        public void BestMatch_AcceptsAMismatchedCreditOnTheOfficialShelf_WhenNoCandidateCarriesTheName()
        {
            var relabelled = Candidate("Never Gonna Give You Up", artist: "RickAstleyVEVO Official");

            Assert.Same(relabelled, Match([relabelled], officialShelf: true));
        }

        [Fact]
        public void BestMatch_StillPrefersTheRightCredit_WhenOneCandidateCarriesIt()
        {
            var wrongCredit = Candidate("Never Gonna Give You Up", artist: "Some Cover Channel");
            var rightCredit = Candidate("Never Gonna Give You Up", artist: "Rick Astley");

            Assert.Same(rightCredit, Match([wrongCredit, rightCredit], officialShelf: true));
        }

        [Fact]
        public void BestMatch_DoesNotCompareCreditsAcrossScripts()
        {
            var japanese = new MatchCandidate("Say It", "ヨルシカ", ThreeMinutesSec);

            Assert.Same(japanese, Match([japanese], title: "Say It", artist: "Yorushika"));
        }

        [Fact]
        public void BestMatch_IgnoresTheCreditWhenTheRequestHasNoRealArtist()
        {
            var candidate = Candidate("Some Song", artist: "Whoever Uploaded It");

            Assert.Same(candidate, Match([candidate], title: "Some Song", artist: "Unknown"));
        }

        [Fact]
        public void BestMatch_PrefersTheCandidateClosestToTheAskedForLength()
        {
            var radioEdit = Candidate("Never Gonna Give You Up", durationSec: 200);
            var albumVersion = Candidate("Never Gonna Give You Up", durationSec: 181);

            Assert.Same(albumVersion, Match([radioEdit, albumVersion]));
        }

        [Fact]
        public void BestMatch_KeepsSearchOrderWhenLengthsAreEquallyClose()
        {
            var first = Candidate("Never Gonna Give You Up", durationSec: 182);
            var second = Candidate("Never Gonna Give You Up", durationSec: 179);

            Assert.Same(first, Match([first, second]));
        }

        [Fact]
        public void BestMatch_IgnoresLengthWhenTheSourceReportsNone()
        {
            var noDuration = Candidate("Never Gonna Give You Up", durationSec: 0);

            Assert.Same(noDuration, Match([noDuration]));
        }

        [Fact]
        public void BestMatch_IgnoresLengthWhenTheRequestHasNone()
        {
            var candidate = Candidate("Never Gonna Give You Up", durationSec: 400);

            Assert.Same(candidate, Match([candidate], durationMs: 0));
        }

        [Fact]
        public void BestMatch_RejectsACandidateFarFromTheAskedForLength()
        {
            Assert.Null(Match([Candidate("Never Gonna Give You Up", durationSec: 3600)]));
        }

        [Fact]
        public void BestMatch_ToleratesASpellingVariantInTheTitle()
        {
            var candidate = Candidate("Tobbss", artist: "Someone");

            Assert.Same(candidate, Match([candidate], title: "Tobbs", artist: "Someone"));
        }

        [Fact]
        public void BestMatch_ReturnsNullForNoCandidates()
        {
            Assert.Null(Match([]));
        }

        [Fact]
        public void BestMatch_PrefersThePlainestTitleWhenNothingElseSeparatesCandidates()
        {
            var radioEdit = Candidate("One More Time (Radio Edit)", artist: "Daft Punk", durationSec: 0);
            var albumVersion = Candidate("One More Time", artist: "Daft Punk", durationSec: 0);

            var best = Match([radioEdit, albumVersion], title: "One More Time", artist: "Daft Punk", durationMs: 0);

            Assert.Same(albumVersion, best);
        }

        [Fact]
        public void BestMatch_RejectsASoloRecordingOfAGroupTrack()
        {
            const string group = "B小町 ルビー（CV：伊駒ゆりえ）、有馬かな（CV：潘めぐみ）、MEMちょ（CV：大久保瑠美）";
            var solo = new MatchCandidate("サインはB -MEMちょ Solo Ver.-", "B小町 MEMちょ（CV：大久保瑠美）", 0);

            Assert.Null(Match([solo], title: "サインはB", artist: group, durationMs: 0, officialShelf: true));
        }

        [Fact]
        public void RankByCreditOnly_KeepsATranslatedTitleThatSharesNoWords()
        {
            const string wanted = "B小町, ルビー(CV:伊駒ゆりえ), 有馬かな(CV:潘めぐみ), MEMちょ(CV:大久保瑠美)";
            const string credited = "B小町 ルビー（CV：伊駒ゆりえ）、有馬かな（CV：潘めぐみ）、MEMちょ（CV：大久保瑠美）";

            var translated = new MatchCandidate("Bのリベンジ", credited, 0, credited);

            Assert.Null(Match([translated], title: "REVENGE OF B", artist: wanted, durationMs: 0, officialShelf: true));
            Assert.Contains(translated, YouTubeMusicMatcher.RankByCreditOnly([translated], wanted));
        }

        [Fact]
        public void RankByCreditOnly_StillRefusesReworksAndForeignCredits()
        {
            const string wanted = "B小町, ルビー(CV:伊駒ゆりえ)";
            const string credited = "B小町 ルビー（CV：伊駒ゆりえ）";

            var instrumental = new MatchCandidate("Bのリベンジ（instrumental）", credited, 0, credited);
            var someoneElse = new MatchCandidate("Bのリベンジ", "歌っちゃ王", 0, "歌っちゃ王");

            Assert.Empty(YouTubeMusicMatcher.RankByCreditOnly([instrumental, someoneElse], wanted));
        }

        [Fact]
        public void RankByCreditOnly_TakesTheClosestLengthRatherThanTheFirstThatFits()
        {
            const string artist = "B小町 ルビー（CV：伊駒ゆりえ）";

            var nearby = new MatchCandidate("深海52Hz", artist, 175, artist);
            var exact = new MatchCandidate("Bのリベンジ", artist, 182, artist);

            var ranked = YouTubeMusicMatcher.RankByCreditOnly([nearby, exact], artist, durationMs: 182_000);

            Assert.Equal(exact, ranked.First());
            Assert.DoesNotContain(nearby, ranked);
        }

        [Fact]
        public void RankByCreditOnly_RefusesToGuessWhenNoArtistWasGiven()
        {
            var candidate = new MatchCandidate("Anything At All", "Whoever", 0);

            Assert.Empty(YouTubeMusicMatcher.RankByCreditOnly([candidate], "Unknown"));
            Assert.Empty(YouTubeMusicMatcher.RankByCreditOnly([candidate], ""));
        }

        [Fact]
        public void RankMatches_FindsATrackWhoseVersionSpotifyHyphenatesAndYouTubeBrackets()
        {
            // Real search rows. Spotify hyphenates the version, YouTube Music brackets it.
            const string wantTitle = "World is Mine - Kaguya&Yachiyo Runami ver. - CPK! Remix";
            const string wantArtist = "ryo (supercell), Kaguya(cv.Yuko Natsuyoshi), Yachiyo Runami(cv.Saori Hayami)";

            var asked = new MatchCandidate(
                "\u30EF\u30FC\u30EB\u30C9\u30A4\u30BA\u30DE\u30A4\u30F3 (\u304B\u3050\u3084&\u6708\u898B\u30E4\u30C1\u30E8 ver.) [CPK! Remix] - World Is Mine (Kaguya&Yachiyo Runami Ver.) [CPK! Remix]",
                "ryo (supercell), Kaguya(cv.Yuko Natsuyoshi), Yachiyo Runami(cv.Saori Hayami)",
                100);

            var otherVersion = new MatchCandidate(
                "\u30EF\u30FC\u30EB\u30C9\u30A4\u30BA\u30DE\u30A4\u30F3 (Anime ver.) [CPK! Remix] - World Is Mine (Anime Ver.) [CPK! Remix] (feat. ChoKaguyaHime)",
                "supercell und Yachiyo Runami(cv.Saori Hayami)",
                100);

            var ranked = YouTubeMusicMatcher.RankMatches([otherVersion, asked], wantTitle, wantArtist, durationMs: 0);

            Assert.Equal(asked, ranked.FirstOrDefault());
        }

        [Fact]
        public void TitleScore_ReadsAQualifierTheCandidateKeepsInBrackets()
        {
            var want = YouTubeMusicMatcher.TitleWords("Sonne - Live Version");

            Assert.Equal(1.0, YouTubeMusicMatcher.TitleScore("Sonne (Live Version)", want));
        }

        [Theory]
        [InlineData("Artist", "-topic", "Artist topic")]
        [InlineData("Artist", "Spider-Man Theme", "Artist Spider-Man Theme")]
        [InlineData("", "Song", "Song")]
        [InlineData("Artist", "", "Artist")]
        public void SearchQuery_StripsTheOperatorMeaningFromALeadingHyphen(string artist, string title, string expected)
        {
            Assert.Equal(expected, YouTubeMusicMatcher.SearchQuery(artist, title));
        }
    }
}
