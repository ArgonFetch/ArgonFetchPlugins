using System.Text.RegularExpressions;

namespace ArgonFetch.Plugin.Spotify
{
    public record MatchCandidate(string Title, string? Artist, long DurationSec, string Details = "");

    public static class YouTubeMusicMatcher
    {
        internal const long DurationToleranceSec = 30L;

        internal const long CreditOnlyToleranceSec = 4L;
        internal const long DurationBucketSec = 5L;
        internal const double MinTitleScore = 0.8;
        internal const string UnknownPlaceholder = "Unknown";

        private static readonly HashSet<string> ReworkMarkers = new(StringComparer.Ordinal)
        {
            "cover", "covers", "karaoke", "instrumental", "remix", "nightcore", "sped", "slowed",
            "reverb", "piano", "acoustic", "tribute", "parody", "mashup", "rendition", "remake",
            "unplugged", "orchestral", "lofi", "8d", "guitar", "violin", "flute", "solo",
        };

        private static readonly Regex Bracketed = new(@"[(\[]([^)\]]*)[)\]]", RegexOptions.Compiled);
        private static readonly Regex NonAlphanumeric = new(@"[^\p{L}\p{N} ]", RegexOptions.Compiled);
        private static readonly Regex BracketedRun = new(@"\(.*?\)|\[.*?]", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex LeadingHyphen = new(@"(^|\s)-+", RegexOptions.Compiled);

        public static MatchCandidate? BestMatch(
            IReadOnlyList<MatchCandidate> candidates,
            string wantTitle,
            string wantArtist,
            long durationMs,
            bool officialShelf = false) =>
            RankMatches(candidates, wantTitle, wantArtist, durationMs, officialShelf).FirstOrDefault();

        public static IReadOnlyList<MatchCandidate> RankMatches(
            IReadOnlyList<MatchCandidate> candidates,
            string wantTitle,
            string wantArtist,
            long durationMs,
            bool officialShelf = false)
        {
            var want = TitleWords(wantTitle);
            var wantArtistWords = Words(RealArtist(wantArtist));

            // Brackets kept: asking for "Sonne (Remix)" must keep the remix.
            var asked = new HashSet<string>(MarkerWords(wantTitle), StringComparer.Ordinal);
            asked.UnionWith(MarkerWords(wantArtist));

            var askedTitleWords = new HashSet<string>(MarkerWords(wantTitle), StringComparer.Ordinal);

            var titled = candidates.Where(candidate =>
                (want.Count == 0 || TitleScore(candidate.Title, want) >= MinTitleScore) &&
                !AddsRework($"{candidate.Title} {BracketedIn(candidate.Details)}", asked))
                .ToList();

            var byArtist = titled.Where(c => ArtistMatches(c.Artist, wantArtistWords)).ToList();
            var viable = officialShelf && byArtist.Count == 0 ? titled : byArtist;

            var ordered = viable.Select((candidate, index) => (candidate, index));

            var timed = viable.Where(c => c.DurationSec > 0).ToList();

            if (durationMs <= 0L || timed.Count == 0)
            {
                return ordered
                    .OrderBy(x => ExtraWords(x.candidate.Title, askedTitleWords))
                    .ThenBy(x => x.index)
                    .Select(x => x.candidate)
                    .ToList();
            }

            var wantSec = durationMs / 1000;

            return ordered
                .Where(x => x.candidate.DurationSec > 0 &&
                            Math.Abs(x.candidate.DurationSec - wantSec) <= DurationToleranceSec)
                .OrderBy(x => Math.Abs(x.candidate.DurationSec - wantSec) / DurationBucketSec)
                .ThenBy(x => ExtraWords(x.candidate.Title, askedTitleWords))
                .ThenBy(x => x.index)
                .Select(x => x.candidate)
                .ToList();
        }

        public static IReadOnlyList<MatchCandidate> RankByCreditOnly(
            IReadOnlyList<MatchCandidate> candidates,
            string wantArtist,
            long durationMs = 0L)
        {
            var wantArtistWords = Words(RealArtist(wantArtist));

            if (wantArtistWords.Count == 0)
                return [];

            var asked = new HashSet<string>(MarkerWords(wantArtist), StringComparer.Ordinal);

            var viable = candidates
                .Where(c => ArtistMatches(c.Artist, wantArtistWords, allowScriptMismatch: false) &&
                            !AddsRework($"{c.Title} {BracketedIn(c.Details)}", asked))
                .ToList();

            var timed = viable.Where(c => c.DurationSec > 0).ToList();

            if (durationMs <= 0L || timed.Count == 0)
                return viable;

            var wantSec = durationMs / 1000;

            return timed
                .Where(c => Math.Abs(c.DurationSec - wantSec) <= CreditOnlyToleranceSec)
                .OrderBy(c => Math.Abs(c.DurationSec - wantSec))
                .ToList();
        }

        // Where duration cannot separate a radio edit from the album version, the plainest
        // title is the one that was asked for.
        internal static int ExtraWords(string candidateTitle, ISet<string> asked) =>
            MarkerWords(candidateTitle).Count(word => !asked.Contains(word));

        internal static bool AddsRework(string candidateTitle, ISet<string> asked) =>
            MarkerWords(candidateTitle).Any(w => ReworkMarkers.Contains(w) && !asked.Contains(w));

        internal static string BracketedIn(string text) =>
            string.Join(" ", Bracketed.Matches(text ?? string.Empty).Select(m => m.Groups[1].Value));

        internal static string RealArtist(string artist) =>
            string.Equals(artist, UnknownPlaceholder, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : artist ?? string.Empty;

        private static HashSet<string> MarkerWords(string s) =>
            new(NonAlphanumeric
                    .Replace((s ?? string.Empty).ToLowerInvariant(), " ")
                    .Split(' ')
                    .Where(w => !string.IsNullOrWhiteSpace(w)),
                StringComparer.Ordinal);

        internal static bool ArtistMatches(string? candidateArtist, ISet<string> wantArtist, bool allowScriptMismatch = true)
        {
            if (wantArtist.Count == 0) return true;

            var have = Words(candidateArtist ?? string.Empty);
            if (have.Count == 0) return true;

            if (allowScriptMismatch && HasLatin(have) != HasLatin(wantArtist)) return true;

            return have.Any(h => wantArtist.Any(w => NearlyEqual(h, w)));
        }

        private static bool HasLatin(IEnumerable<string> words) =>
            words.Any(word => word.Any(c => c >= 'a' && c <= 'z'));

        // Brackets kept: Spotify hyphenates a version into the name where YouTube Music
        // brackets it, so dropping them scored an exact match a third. Extra words never count
        // against a candidate, so keeping them cannot cost a match.
        internal static double TitleScore(string candidateTitle, ISet<string> want)
        {
            if (want.Count == 0) return 0.0;

            var have = MarkerWords(candidateTitle);
            return want.Count(w => have.Any(h => NearlyEqual(h, w))) / (double)want.Count;
        }

        private static bool NearlyEqual(string a, string b)
        {
            if (a == b) return true;
            if (Math.Min(a.Length, b.Length) < 4 || Math.Abs(a.Length - b.Length) > 1) return false;

            var (longer, shorter) = a.Length >= b.Length ? (a, b) : (b, a);
            var i = 0;
            var j = 0;
            var edits = 0;

            while (i < longer.Length && j < shorter.Length)
            {
                if (longer[i] == shorter[j])
                {
                    i++;
                    j++;
                }
                else
                {
                    if (++edits > 1) return false;
                    i++;
                    if (longer.Length == shorter.Length) j++;
                }
            }

            return edits + (longer.Length - i) + (shorter.Length - j) <= 1;
        }

        // YouTube reads a leading "-word" as "exclude everything containing word", which
        // emptied the shelf for tracks named that way. Spider-Man keeps its own.
        public static string SearchQuery(string artist, string title)
        {
            var joined = string.Join(" ", new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return LeadingHyphen.Replace(joined, "$1").Trim();
        }

        // Stops at the feature credit: sources write the guest into the name where YouTube
        // Music brackets it, and normalisation then strips it off the candidate.
        internal static HashSet<string> TitleWords(string title)
        {
            var beforeCredit = SplitOnFeature(Normalize(title));
            var words = Words(beforeCredit);
            return words.Count > 0 ? words : Words(title);
        }

        private static string SplitOnFeature(string normalized)
        {
            var featIndex = normalized.IndexOf(" feat ", StringComparison.Ordinal);
            var ftIndex = normalized.IndexOf(" ft ", StringComparison.Ordinal);

            var index = featIndex >= 0 && ftIndex >= 0
                ? Math.Min(featIndex, ftIndex)
                : Math.Max(featIndex, ftIndex);

            return index >= 0 ? normalized[..index] : normalized;
        }

        private static HashSet<string> Words(string s) =>
            new(Normalize(s).Split(' ').Where(w => !string.IsNullOrWhiteSpace(w)), StringComparer.Ordinal);

        internal static string Normalize(string s)
        {
            var lowered = (s ?? string.Empty).ToLowerInvariant();
            var withoutBrackets = BracketedRun.Replace(lowered, " ");
            var alphanumeric = NonAlphanumeric.Replace(withoutBrackets, " ");
            return Whitespace.Replace(alphanumeric, " ").Trim();
        }
    }
}
