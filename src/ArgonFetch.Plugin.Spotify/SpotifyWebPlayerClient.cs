using ArgonFetch.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArgonFetch.Plugin.Spotify
{
    public interface ISpotifyWebPlayerClient
    {
        Task<IReadOnlyList<SpotifyCollectionItem>?> TryGetPlaylistTracksAsync(string playlistId, CancellationToken cancellationToken = default);
    }

    // The embed page silently stops at a hundred entries. This pages to the end, with an
    // anonymous token; the password secret and query hash both rotate, so both are fetched.
    public class SpotifyWebPlayerClient : ISpotifyWebPlayerClient
    {
        private const string SecretsUrl = "https://code.thetadev.de/ThetaDev/spotify-secrets/raw/branch/main/secrets/secretDict.json";
        private const string ServerTimeUrl = "https://open.spotify.com/api/server-time";
        private const string WebPlayerUrl = "https://open.spotify.com/";
        private const string PathfinderUrl = "https://api-partner.spotify.com/pathfinder/v2/query";

        private const string TokenCacheKey = "spotify-web-player-token";
        private const string SecretsCacheKey = "spotify-web-player-secrets";
        private const string HashCacheKey = "spotify-web-player-hash";

        private const int PageSize = 100;

        private const int MaxTracks = 10_000;

        private const int MaxBundlesToSearch = 5;

        private static readonly Regex BundleUrls = new(
            @"https://open\.spotifycdn\.com/cdn/build/web-player/[^""'\s]+\.js",
            RegexOptions.Compiled);

        // Anything not looking like a desktop browser is served the mobile page, which
        // carries no bundle this can read.
        private const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36";

        private static readonly Regex PersistedQuery = new(
            @"""(\w+)"",""query"",""([0-9a-f]{64})""",
            RegexOptions.Compiled);

        private readonly IProviderContext _context;
        private readonly IMemoryCache _cache;
        private readonly ILogger _logger;

        public SpotifyWebPlayerClient(IProviderContext context)
        {
            _context = context;
            _cache = context.Cache;
            _logger = context.Logger;
        }

        public async Task<IReadOnlyList<SpotifyCollectionItem>?> TryGetPlaylistTracksAsync(
            string playlistId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var token = await GetAccessTokenAsync(cancellationToken);
                var hash = await GetFetchPlaylistHashAsync(cancellationToken);

                var tracks = new List<SpotifyCollectionItem>();
                var total = int.MaxValue;
                var repaired = false;

                while (tracks.Count < total && tracks.Count < MaxTracks)
                {
                    var page = await FetchPageAsync(playlistId, tracks.Count, token, hash, cancellationToken);

                    if (page is { Rejected: true } && !repaired)
                    {
                        repaired = true;
                        token = await GetAccessTokenAsync(cancellationToken, forceRefresh: true);
                        continue;
                    }

                    if (page is null or { Rejected: true })
                        return tracks.Count > 0 ? tracks : null;

                    total = page.Total;
                    tracks.AddRange(page.Items);

                    if (page.Items.Count == 0)
                        break;
                }

                _logger.LogInformation("Read {Count} of {Total} tracks for playlist {Id}", tracks.Count, total, playlistId);

                return tracks;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read playlist {Id} through the web player API", playlistId);
                return null;
            }
        }

        private async Task<PlaylistPage?> FetchPageAsync(
            string playlistId,
            int offset,
            string token,
            string hash,
            CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new
            {
                variables = new
                {
                    uri = $"spotify:playlist:{playlistId}",
                    offset,
                    limit = PageSize,
                    enableWatchFeedEntrypoint = false
                },
                operationName = "fetchPlaylist",
                extensions = new { persistedQuery = new { version = 1, sha256Hash = hash } }
            });

            var httpClient = _context.CreateHttpClient(rotateProxy: false);

            using var request = new HttpRequestMessage(HttpMethod.Post, PathfinderUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("accept", "application/json");

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogDebug("Spotify refused the session at offset {Offset}; refreshing it once.", offset);
                return PlaylistPage.SessionRejected;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Spotify's playlist API answered {Status} at offset {Offset}", (int)response.StatusCode, offset);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            {
                _logger.LogWarning("Spotify's playlist API reported: {Message}", errors[0].GetProperty("message").GetString());
                return null;
            }

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("playlistV2", out var playlist) ||
                !playlist.TryGetProperty("content", out var content))
            {
                return null;
            }

            var total = content.TryGetProperty("totalCount", out var totalCount) ? totalCount.GetInt32() : 0;
            var items = new List<SpotifyCollectionItem>();

            if (content.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in array.EnumerateArray())
                {
                    var track = ReadTrack(entry);

                    if (track is not null)
                        items.Add(track);
                }
            }

            return new PlaylistPage(items, total, Rejected: false);
        }

        private record PlaylistPage(IReadOnlyList<SpotifyCollectionItem> Items, int Total, bool Rejected)
        {
            public static readonly PlaylistPage SessionRejected = new([], 0, Rejected: true);
        }

        private static SpotifyCollectionItem? ReadTrack(JsonElement entry)
        {
            if (!entry.TryGetProperty("itemV2", out var itemV2) || !itemV2.TryGetProperty("data", out var data))
                return null;

            var uri = data.TryGetProperty("uri", out var uriValue) ? uriValue.GetString() : null;
            var id = uri?.Split(':').LastOrDefault();

            if (string.IsNullOrWhiteSpace(id) || uri?.StartsWith("spotify:track:", StringComparison.Ordinal) != true)
                return null;

            var name = data.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;

            var artists = new List<string>();

            if (data.TryGetProperty("artists", out var artistsValue) &&
                artistsValue.TryGetProperty("items", out var artistItems))
            {
                foreach (var artist in artistItems.EnumerateArray())
                {
                    if (artist.TryGetProperty("profile", out var profile) &&
                        profile.TryGetProperty("name", out var artistName))
                    {
                        artists.Add(artistName.GetString() ?? string.Empty);
                    }
                }
            }

            var durationMs = data.TryGetProperty("trackDuration", out var duration) &&
                             duration.TryGetProperty("totalMilliseconds", out var ms)
                ? ms.GetInt64()
                : 0L;

            return new SpotifyCollectionItem(
                name ?? "Unknown",
                string.Join(", ", artists.Where(a => a.Length > 0)),
                durationMs,
                $"https://open.spotify.com/track/{id}");
        }

        private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            if (!forceRefresh && _cache.TryGetValue(_context.CacheKey(TokenCacheKey), out string? cached) && !string.IsNullOrEmpty(cached))
                return cached;

            var httpClient = _context.CreateHttpClient(rotateProxy: false);

            var response = await MintTokenAsync(httpClient, refreshSecret: false, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Spotify refused the token request ({Status}); re-reading the password secret.", (int)response.StatusCode);
                response.Dispose();
                response = await MintTokenAsync(httpClient, refreshSecret: true, cancellationToken);
            }

            using var _ = response;
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var token = document.RootElement.GetProperty("accessToken").GetString()
                ?? throw new InvalidOperationException("Spotify returned no access token.");

            var expiresAt = document.RootElement.TryGetProperty("accessTokenExpirationTimestampMs", out var expiry)
                ? DateTimeOffset.FromUnixTimeMilliseconds(expiry.GetInt64())
                : DateTimeOffset.UtcNow.AddMinutes(30);

            _cache.Set(_context.CacheKey(TokenCacheKey), token, expiresAt.AddMinutes(-1));

            return token;
        }

        private async Task<HttpResponseMessage> MintTokenAsync(HttpClient httpClient, bool refreshSecret, CancellationToken cancellationToken)
        {
            var (version, secret) = await GetTotpSecretAsync(httpClient, refreshSecret, cancellationToken);
            var code = GenerateTotp(secret, await GetSpotifyTimeAsync(httpClient, cancellationToken));

            var url = $"https://open.spotify.com/api/token?reason=init&productType=web-player" +
                      $"&totp={code}&totpServer={code}&totpVer={version}";

            return await httpClient.GetAsync(url, cancellationToken);
        }

        // Rotated by Spotify, so it is read from a mirror rather than written down here.
        private async Task<(int Version, IReadOnlyList<int> Secret)> GetTotpSecretAsync(HttpClient httpClient, bool forceRefresh, CancellationToken cancellationToken)
        {
            if (!forceRefresh && _cache.TryGetValue(_context.CacheKey(SecretsCacheKey), out (int, IReadOnlyList<int>) cached))
                return cached;

            using var response = await httpClient.GetAsync(SecretsUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            var newest = document.RootElement.EnumerateObject()
                .Select(property => int.TryParse(property.Name, out var version) ? version : 0)
                .DefaultIfEmpty(0)
                .Max();

            if (newest == 0)
                throw new InvalidOperationException("The secrets mirror returned nothing usable.");

            var secret = document.RootElement.GetProperty(newest.ToString())
                .EnumerateArray()
                .Select(value => value.GetInt32())
                .ToList();

            var result = (newest, (IReadOnlyList<int>)secret);
            _cache.Set(_context.CacheKey(SecretsCacheKey), result, TimeSpan.FromHours(12));

            return result;
        }

        // The password is valid for thirty seconds, so a drifted clock mints refused codes.
        private async Task<long> GetSpotifyTimeAsync(HttpClient httpClient, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await httpClient.GetAsync(ServerTimeUrl, cancellationToken);
                response.EnsureSuccessStatusCode();

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

                return document.RootElement.GetProperty("serverTime").GetInt64();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not read Spotify's clock; using this machine's.");
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
        }

        internal static string GenerateTotp(IReadOnlyList<int> secret, long unixSeconds)
        {
            var mixed = string.Concat(secret.Select((value, index) => (value ^ ((index % 33) + 9)).ToString()));
            var key = Encoding.UTF8.GetBytes(mixed);

            var counter = unixSeconds / 30;
            var counterBytes = BitConverter.GetBytes(counter);

            if (BitConverter.IsLittleEndian)
                Array.Reverse(counterBytes);

            using var hmac = new HMACSHA1(key);
            var digest = hmac.ComputeHash(counterBytes);

            var offset = digest[^1] & 0x0F;
            var binary = ((digest[offset] & 0x7F) << 24)
                       | (digest[offset + 1] << 16)
                       | (digest[offset + 2] << 8)
                       | digest[offset + 3];

            return (binary % 1_000_000).ToString("D6");
        }

        private static async Task<string> GetAsBrowserAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        private async Task<string> GetFetchPlaylistHashAsync(CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(_context.CacheKey(HashCacheKey), out string? cached) && !string.IsNullOrEmpty(cached))
                return cached;

            var httpClient = _context.CreateHttpClient(rotateProxy: false);

            var home = await GetAsBrowserAsync(httpClient, WebPlayerUrl, cancellationToken);

            var scripts = BundleUrls.Matches(home)
                .Select(match => match.Value)
                .Distinct()
                .OrderByDescending(url => url.Contains("/web-player.", StringComparison.Ordinal))
                .Take(MaxBundlesToSearch)
                .ToList();

            if (scripts.Count == 0)
                throw new InvalidOperationException("The web player page listed no bundles.");

            foreach (var script in scripts)
            {
                string bundle;

                try
                {
                    bundle = await GetAsBrowserAsync(httpClient, script, cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogDebug(ex, "Could not read {Script} while looking for the playlist query.", script);
                    continue;
                }

                foreach (Match match in PersistedQuery.Matches(bundle))
                {
                    if (match.Groups[1].Value != "fetchPlaylist")
                        continue;

                    var hash = match.Groups[2].Value;
                    _cache.Set(_context.CacheKey(HashCacheKey), hash, TimeSpan.FromHours(6));

                    return hash;
                }
            }

            throw new InvalidOperationException("The web player bundle carried no fetchPlaylist query.");
        }
    }
}
