using ArgonFetch.Abstractions;
using Microsoft.Extensions.Logging;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Search;

[assembly: ArgonFetchPlugin("spotify", ArgonFetchPluginAttribute.CurrentAbi, Name = "Spotify")]

namespace ArgonFetch.Plugin.Spotify
{
    /// <summary>
    /// Spotify links.
    /// <para>
    /// Spotify serves no audio anyone can download, so a track is described from Spotify and then
    /// fetched from wherever the same recording actually is - which is a redirect, not a download,
    /// and why this plugin never returns media of its own. A playlist or an album is listed and
    /// left at that: resolving a thousand entries to show a list would take the better part of an
    /// hour and throw nearly all of it away.
    /// </para>
    /// </summary>
    public sealed class SpotifyProvider : ISourceProvider
    {
        // Enough to hold the album version when search leads with a radio edit and a remaster,
        // and few enough that a mistyped query does not drag twenty rows through matching.
        private const int SearchResultsToConsider = 20;

        // Wrong-recording candidates come in runs - a radio edit, a remaster and a twelve-inch
        // mix all sit above the album version - so checking a couple past the leader is worth a
        // probe each. Beyond that the search itself was wrong.
        private const int MaxVerificationAttempts = 3;

        // A release and its Spotify entry differ by a second or two of trailing silence. A radio
        // edit differs by a minute, which is what this has to catch.
        private const double VerificationToleranceSec = 12.0;

        public string Id => "spotify";

        // open.spotify.com and the intl-xx variants of it, plus the spotify.link shortener.
        // Declared rather than matched in code: the host compiles these once and does the
        // matching itself, so there is no code here to get wrong.
        public IReadOnlyList<string> UrlPatterns =>
        [
            @"^https?://([\w-]+\.)*spotify\.com/",
            @"^https?://([\w-]+\.)*spotify\.link/",
        ];

        public async Task<ProviderOutcome> PrepareAsync(Uri url, IProviderContext context, CancellationToken cancellationToken)
        {
            var segments = url.AbsolutePath.Trim('/').Split('/');
            var metadata = new SpotifyMetadataService(context, new SpotifyWebPlayerClient(context));

            if (segments.Contains("playlist") || segments.Contains("album"))
                return await ListAsync(url, metadata, context, cancellationToken);

            if (segments.Contains("track"))
                return await FindRecordingAsync(url, metadata, context, cancellationToken);

            // A link to an artist, a show or something not yet invented. Nothing here knows what
            // to do with it, and saying so lets the ordinary fetch have its turn.
            return ProviderOutcome.Declined;
        }

        private static async Task<ProviderOutcome> ListAsync(
            Uri url,
            SpotifyMetadataService metadata,
            IProviderContext context,
            CancellationToken cancellationToken)
        {
            var collection = await metadata.GetCollectionAsync(url.ToString(), cancellationToken);

            return ProviderOutcome.Listing(new CollectionResult(
                collection.Title,
                collection.Author,
                collection.CoverUrl,
                collection.Items
                    .Select(item => new CollectionEntry(
                        new Uri(item.TrackUrl),
                        item.Title,
                        item.Artist,
                        collection.CoverUrl))
                    .ToList())
            {
                MayBeTruncated = collection.MayBeTruncated
            });
        }

        /// <summary>
        /// Finds the recording somewhere it can actually be fetched from, and points the download
        /// at that while keeping Spotify's own title and credit.
        /// </summary>
        private async Task<ProviderOutcome> FindRecordingAsync(
            Uri url,
            SpotifyMetadataService metadata,
            IProviderContext context,
            CancellationToken cancellationToken)
        {
            var track = await metadata.GetTrackAsync(url.ToString(), cancellationToken);
            var searchQuery = YouTubeMusicMatcher.SearchQuery(track.Artist, track.Title);

            var results = (await new YouTubeMusicClient()
                    .SearchAsync(searchQuery, SearchCategory.Songs)
                    .FetchItemsAsync(0, SearchResultsToConsider, cancellationToken))
                .OfType<SongSearchResult>()
                .ToList();

            // Taking the first hit matches covers, karaoke versions and instrumentals as readily
            // as the real recording, so score the candidates instead.
            var candidates = results
                .Select(result => new MatchCandidate(
                    result.Name,
                    string.Join(", ", result.Artists.Select(artist => artist.Name)),
                    (long)result.Duration.TotalSeconds,
                    // The release, which is where an instrumental or a solo cut declares itself.
                    result.Album?.Name ?? string.Empty))
                .ToList();

            var ranked = YouTubeMusicMatcher.RankMatches(
                candidates, track.Title, track.Artist, track.DurationMs, officialShelf: true);

            var found = await VerifyAsync(ranked, candidates, results, track, context, cancellationToken);

            if (found is null)
            {
                // Titles are often translated - Spotify says "REVENGE OF B" where YouTube Music
                // says the Japanese original - and then no candidate shares a single word with
                // what was asked for. The credit still matches, and the length decides.
                var byCredit = YouTubeMusicMatcher.RankByCreditOnly(candidates, track.Artist, track.DurationMs);

                found = await VerifyAsync(byCredit, candidates, results, track, context, cancellationToken, requireDuration: true);
            }

            if (found is null)
            {
                context.Logger.LogInformation("Nothing on YouTube Music matched '{Query}'", searchQuery);

                // Declining rather than failing: the ordinary fetch will report that it cannot
                // read a Spotify link, which is a truer answer than an error about matching.
                return ProviderOutcome.Declined;
            }

            return ProviderOutcome.Rewrite(
                found,
                new MediaTags(track.Title, track.Artist),
                track.CoverUrl);
        }

        /// <summary>
        /// Walks the ranked candidates and keeps the first whose real length agrees with Spotify.
        /// <para>
        /// A safety net rather than the mechanism: search reports durations, so ranking usually
        /// settles which recording this is. Probing confirms it, because a length that search got
        /// wrong would otherwise be discovered by whoever opened the file.
        /// </para>
        /// </summary>
        private static async Task<Uri?> VerifyAsync(
            IReadOnlyList<MatchCandidate> ranked,
            IReadOnlyList<MatchCandidate> candidates,
            IReadOnlyList<SongSearchResult> results,
            SpotifyTrackMetadata track,
            IProviderContext context,
            CancellationToken cancellationToken,
            bool requireDuration = false)
        {
            Uri? first = null;

            foreach (var candidate in ranked.Take(MaxVerificationAttempts))
            {
                var index = candidates.ToList().IndexOf(candidate);

                if (index < 0)
                    continue;

                var url = new Uri($"https://music.youtube.com/watch?v={results[index].Id}");

                first ??= url;

                var probe = await context.ProbeAsync(url, cancellationToken);

                if (probe?.DurationSeconds is > 0 && track.DurationMs > 0 &&
                    Math.Abs(probe.DurationSeconds.Value - track.DurationMs / 1000.0) <= VerificationToleranceSec)
                {
                    return url;
                }
            }

            // Nothing was confirmed. On the ordinary path the leader had already matched on title
            // and credit, so it is still the best answer available; on the credit-only path there
            // is nothing left to identify it by, and a wrong recording is worse than none.
            return requireDuration ? null : first;
        }
    }
}
