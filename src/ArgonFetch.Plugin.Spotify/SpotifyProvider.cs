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

            var client = new YouTubeMusicClient();

            var found = await SearchShelfAsync(client, SearchCategory.Songs, searchQuery, track, context, cancellationToken);

            // Taken-down tracks survive only as reuploads, which sit on the video shelf.
            found ??= await SearchShelfAsync(client, SearchCategory.Videos, searchQuery, track, context, cancellationToken);

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

        private static async Task<Uri?> SearchShelfAsync(
            YouTubeMusicClient client,
            SearchCategory category,
            string searchQuery,
            SpotifyTrackMetadata track,
            IProviderContext context,
            CancellationToken cancellationToken)
        {
            var results = (await client
                    .SearchAsync(searchQuery, category)
                    .FetchItemsAsync(0, SearchResultsToConsider, cancellationToken))
                .Select(ToResult)
                .OfType<ShelfResult>()
                .ToList();

            var candidates = results.Select(result => result.Candidate).ToList();
            var ids = results.Select(result => result.Id).ToList();

            // Anyone can upload a video, so only the song shelf may waive the credit.
            var officialShelf = category == SearchCategory.Songs;

            var ranked = YouTubeMusicMatcher.RankMatches(
                candidates, track.Title, track.Artist, track.DurationMs, officialShelf);

            var found = await VerifyAsync(ranked, candidates, ids, track, context, cancellationToken);

            if (found is null && officialShelf)
            {
                var byCredit = YouTubeMusicMatcher.RankByCreditOnly(candidates, track.Artist, track.DurationMs);

                found = await VerifyAsync(byCredit, candidates, ids, track, context, cancellationToken, requireDuration: true);
            }

            return found;
        }

        private sealed record ShelfResult(string Id, MatchCandidate Candidate);

        private static ShelfResult? ToResult(SearchResult result) => result switch
        {
            SongSearchResult song => new(song.Id, new MatchCandidate(
                song.Name,
                string.Join(", ", song.Artists.Select(artist => artist.Name)),
                (long)song.Duration.TotalSeconds,
                song.Album?.Name ?? string.Empty)),
            VideoSearchResult video => new(video.Id, new MatchCandidate(
                video.Name,
                string.Join(", ", video.Artists.Select(artist => artist.Name)),
                (long)video.Duration.TotalSeconds)),
            _ => null,
        };

        private static async Task<Uri?> VerifyAsync(
            IReadOnlyList<MatchCandidate> ranked,
            IReadOnlyList<MatchCandidate> candidates,
            IReadOnlyList<string> ids,
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

                var url = new Uri($"https://music.youtube.com/watch?v={ids[index]}");

                var probe = await context.ProbeAsync(url, cancellationToken);

                if (probe?.DurationSeconds is not > 0 || track.DurationMs <= 0)
                {
                    first ??= url;
                    continue;
                }

                // A measured length that disagrees is a different recording, never a fallback.
                if (Math.Abs(probe.DurationSeconds.Value - track.DurationMs / 1000.0) <= VerificationToleranceSec)
                    return url;
            }

            return requireDuration ? null : first;
        }
    }
}
