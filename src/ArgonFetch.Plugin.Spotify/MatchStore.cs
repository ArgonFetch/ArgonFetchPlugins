using System.Collections.Concurrent;
using System.Text.Json;

namespace ArgonFetch.Plugin.Spotify
{
    public record StoredMatch(string VideoId, string Title, string Artist, string? CoverUrl);

    // A track's recording does not move, so a match outlives restarts. The file sits beside the
    // plugin folders, which the installer replaces on update but never touches loose files next to.
    // ponytail: one JSON file rewritten per new match - SQLite if it grows past tens of thousands.
    internal static class MatchStore
    {
        private static readonly string? FilePath = PathBesidePlugins("spotify-matches.json");

        private static readonly Lazy<ConcurrentDictionary<string, StoredMatch>> Matches = new(Load);

        private static readonly Lock WriteLock = new();

        public static StoredMatch? Find(string trackId) => Matches.Value.GetValueOrDefault(trackId);

        public static void Remember(string trackId, StoredMatch match)
        {
            Matches.Value[trackId] = match;

            if (FilePath is null)
                return;

            try
            {
                lock (WriteLock)
                {
                    var temporary = FilePath + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(Matches.Value));
                    File.Move(temporary, FilePath, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The copy in memory still answers until the next restart.
            }
        }

        private static ConcurrentDictionary<string, StoredMatch> Load()
        {
            try
            {
                if (FilePath is not null && File.Exists(FilePath))
                    return JsonSerializer.Deserialize<ConcurrentDictionary<string, StoredMatch>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A damaged file costs the matches, not the plugin.
            }

            return new();
        }

        private static string? PathBesidePlugins(string name)
        {
            var pluginFolder = Path.GetDirectoryName(typeof(MatchStore).Assembly.Location);
            var root = string.IsNullOrEmpty(pluginFolder) ? null : Path.GetDirectoryName(pluginFolder);

            return root is null ? null : Path.Combine(root, name);
        }
    }
}
