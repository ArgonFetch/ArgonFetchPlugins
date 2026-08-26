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

            // Read the request the same way as the candidate, brackets included: asking for "Sonne
            // (Remix)" must keep the remix, and that marker only survives in the bracket-keeping split.
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

        /// <summary>
        /// Words the candidate's title carries that nobody asked for.
        /// <para>
        /// Search returns the radio edit, the remaster and the twelve-inch mix alongside the
        /// album version, all with the right title and the right credit. Where no duration is
        /// available to separate them, the plainest title is the one that was asked for.
        /// Counted with brackets kept, because that is where the qualifier lives.
        /// </para>
        /// </summary>
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

        /// <summary>
        /// Share of the wanted title's words the candidate carries.
        /// <para>
        /// Counted with the candidate's brackets kept. The two sides write a version qualifier
        /// differently - Spotify hyphenates it into the name, "World is Mine - Kaguya&amp;Yachiyo
        /// Runami ver. - CPK! Remix", where YouTube Music brackets it - so dropping the brackets
        /// erased the half of the candidate that the request was mostly made of, and an exact
        /// match scored a third. Keeping them cannot cost a match either way: the score measures
        /// how much of the wanted title the candidate carries, so words the candidate has and
        /// nobody asked for were never counted against it.
        /// </para>
        /// </summary>
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

        /// <summary>
        /// What to search for, with the operator meaning taken out of a leading hyphen.
        /// <para>
        /// YouTube reads "-word" as "exclude everything containing word". A track whose name opens
        /// with a hyphen therefore asked search to drop every result carrying that word, and the
        /// shelf came back empty. Only a hyphen that opens a word is an operator, so a name like
        /// Spider-Man keeps its own.
        /// </para>
        /// </summary>
        public static string SearchQuery(string artist, string title)
        {
            var joined = string.Join(" ", new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return LeadingHyphen.Replace(joined, "$1").Trim();
        }

        /// <summary>
        /// The words of the wanted title a candidate has to carry, which stops at the feature credit.
        /// <para>
        /// Some sources write the guest into the track name where YouTube Music puts it in brackets
        /// that Normalize then strips off the candidate, so a three-word wanted title was scored
        /// against a one-word candidate and never came near MinTitleScore. Only the part before the
        /// credit is required; a candidate that does spell the guest out still matches, because the
        /// score measures how much of the wanted title the candidate carries and never penalises
        /// extra words.
        /// </para>
        /// </summary>
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
