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

            var trackAt = Array.IndexOf(segments, "track");

            if (trackAt >= 0)
            {
                var trackId = trackAt + 1 < segments.Length ? segments[trackAt + 1] : null;

                return await FindRecordingAsync(url, trackId, metadata, context, cancellationToken);
            }

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
            string? trackId,
            SpotifyMetadataService metadata,
            IProviderContext context,
            CancellationToken cancellationToken)
        {
            if (trackId is not null && MatchStore.Find(trackId) is { } known)
            {
                return ProviderOutcome.Rewrite(
                    WatchUrl(known.VideoId),
                    new MediaTags(known.Title, known.Artist),
                    known.CoverUrl);
            }

            var track = await metadata.GetTrackAsync(url.ToString(), cancellationToken);
            var searchQuery = YouTubeMusicMatcher.SearchQuery(track.Artist, track.Title);

            var client = new YouTubeMusicClient();

            // Taken-down tracks survive only as reuploads, which sit on the video shelf. It is read
            // alongside the song shelf, because waiting to need it costs a round trip.
            var songs = ReadShelfAsync(client, SearchCategory.Songs, searchQuery, context, cancellationToken);
            var videos = ReadShelfAsync(client, SearchCategory.Videos, searchQuery, context, cancellationToken);

            var found = await MatchShelfAsync(await songs, officialShelf: true, track, context, cancellationToken)
                     ?? await MatchShelfAsync(await videos, officialShelf: false, track, context, cancellationToken);

            if (found is null)
            {
                context.Logger.LogInformation("Nothing on YouTube Music matched '{Query}'", searchQuery);

                return ProviderOutcome.Declined;
            }

            if (trackId is not null)
                MatchStore.Remember(trackId, new StoredMatch(found, track.Title, track.Artist, track.CoverUrl));

            return ProviderOutcome.Rewrite(
                WatchUrl(found),
                new MediaTags(track.Title, track.Artist),
                track.CoverUrl);
        }

        private static Uri WatchUrl(string videoId) => new($"https://music.youtube.com/watch?v={videoId}");

        private static async Task<IReadOnlyList<SearchResult>> ReadShelfAsync(
            YouTubeMusicClient client,
            SearchCategory category,
            string searchQuery,
            IProviderContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                return await client
                    .SearchAsync(searchQuery, category)
                    .FetchItemsAsync(0, SearchResultsToConsider, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One row YouTubeMusicAPI cannot parse throws away the whole shelf; the other may still have it.
                context.Logger.LogWarning(ex, "The {Category} shelf for '{Query}' could not be read", category, searchQuery);

                return [];
            }
        }

        private static async Task<string?> MatchShelfAsync(
            IReadOnlyList<SearchResult> page,
            bool officialShelf,
            SpotifyTrackMetadata track,
            IProviderContext context,
            CancellationToken cancellationToken)
        {
            var results = page
                .Select(ToResult)
                .OfType<ShelfResult>()
                .ToList();

            var candidates = results.Select(result => result.Candidate).ToList();
            var ids = results.Select(result => result.Id).ToList();

            // Anyone can upload a video, so only the song shelf may waive the credit.
            var ranked = YouTubeMusicMatcher.RankMatches(
                candidates, track.Title, track.Artist, track.DurationMs, officialShelf);

            var found = await VerifyAsync(ranked, candidates, ids, track, context, cancellationToken);

            if (found is null && officialShelf)
            {
                var byCredit = YouTubeMusicMatcher.RankByCreditOnly(candidates, track.Artist, track.DurationMs);

                found = await VerifyAsync(byCredit, candidates, ids, track, context, cancellationToken, requireDuration: true);
            }

            if (found is null && !officialShelf)
            {
                var byTitleCredit = YouTubeMusicMatcher.RankMatches(
                    candidates, track.Title, track.Artist, track.DurationMs, creditInTitle: true);

                found = await VerifyAsync(byTitleCredit, candidates, ids, track, context, cancellationToken, requireDuration: true);
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

        private static async Task<string?> VerifyAsync(
            IReadOnlyList<MatchCandidate> ranked,
            IReadOnlyList<MatchCandidate> candidates,
            IReadOnlyList<string> ids,
            SpotifyTrackMetadata track,
            IProviderContext context,
            CancellationToken cancellationToken,
            bool requireDuration = false)
        {
            string? first = null;

            foreach (var candidate in ranked.Take(MaxVerificationAttempts))
            {
                var index = candidates.ToList().IndexOf(candidate);

                if (index < 0)
                    continue;

                var id = ids[index];

                // The shelf lists the length of this very video, so yt-dlp is asked only when it does not.
                var seconds = candidate.DurationSec > 0
                    ? candidate.DurationSec
                    : (await context.ProbeAsync(WatchUrl(id), cancellationToken))?.DurationSeconds;

                if (seconds is not > 0 || track.DurationMs <= 0)
                {
                    first ??= id;
                    continue;
                }

                // A measured length that disagrees is a different recording, never a fallback.
                if (Math.Abs(seconds.Value - track.DurationMs / 1000.0) <= VerificationToleranceSec)
                    return id;
            }

            return requireDuration ? null : first;
        }
    }
}
