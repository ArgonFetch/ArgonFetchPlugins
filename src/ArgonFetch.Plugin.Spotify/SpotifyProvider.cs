using ArgonFetch.Abstractions;
using Microsoft.Extensions.Logging;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Search;

[assembly: ArgonFetchPlugin("spotify", ArgonFetchPluginAttribute.CurrentAbi, Name = "Spotify")]

namespace ArgonFetch.Plugin.Spotify
{
    public sealed class SpotifyProvider : ISourceProvider
    {
        private const int SearchResultsToConsider = 20;

        private const int MaxVerificationAttempts = 3;

        private const double VerificationToleranceSec = 12.0;

        public string Id => "spotify";

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

            var candidates = results
                .Select(result => new MatchCandidate(
                    result.Name,
                    string.Join(", ", result.Artists.Select(artist => artist.Name)),
                    (long)result.Duration.TotalSeconds,
                    result.Album?.Name ?? string.Empty))
                .ToList();

            var ranked = YouTubeMusicMatcher.RankMatches(
                candidates, track.Title, track.Artist, track.DurationMs, officialShelf: true);

            var found = await VerifyAsync(ranked, candidates, results, track, context, cancellationToken);

            if (found is null)
            {
                var byCredit = YouTubeMusicMatcher.RankByCreditOnly(candidates, track.Artist, track.DurationMs);

                found = await VerifyAsync(byCredit, candidates, results, track, context, cancellationToken, requireDuration: true);
            }

            if (found is null)
            {
                context.Logger.LogInformation("Nothing on YouTube Music matched '{Query}'", searchQuery);

                return ProviderOutcome.Declined;
            }

            return ProviderOutcome.Rewrite(
                found,
                new MediaTags(track.Title, track.Artist),
                track.CoverUrl);
        }

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

            return requireDuration ? null : first;
        }
    }
}
