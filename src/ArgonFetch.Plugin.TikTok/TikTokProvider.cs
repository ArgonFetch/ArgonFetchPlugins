using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ArgonFetch.Abstractions;

[assembly: ArgonFetchPlugin("tiktok", ArgonFetchPluginAttribute.CurrentAbi, Name = "TikTok")]

namespace ArgonFetch.Plugin.TikTok
{
    /// <summary>
    /// TikTok videos, fetched without the watermark.
    /// <para>
    /// Fetched here rather than redirected: what comes back is one file already carrying both
    /// picture and sound, so there is nothing for the ordinary path to add.
    /// </para>
    /// </summary>
    public sealed class TikTokProvider : ISourceProvider
    {
        // The site that does the actual work. Kept in one place because it is the part most
        // likely to need replacing - these services come and go.
        private const string Service = "https://tmate.cc";

        private static readonly HtmlParser Parser = new();

        public string Id => "tiktok";

        // tiktok.com and the vm./vt. short links people actually share.
        public IReadOnlyList<string> UrlPatterns =>
        [
            @"^https?://([\w-]+\.)*tiktok\.com/",
        ];

        public async Task<ProviderOutcome> PrepareAsync(Uri url, IProviderContext context, CancellationToken cancellationToken)
        {
            // Not rotated: the token below is issued against a session cookie, and answering
            // from a different address than the one it was handed to is refused.
            var httpClient = context.CreateHttpClient(rotateProxy: false);

            var (token, session) = await StartSessionAsync(httpClient, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Service}/action")
            {
                Content = new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("url", url.ToString()),
                    new KeyValuePair<string, string>("token", token),
                ])
            };

            // Set per request rather than on the client: the client is shared with everything
            // else fetching media, and a cookie left on it would travel to unrelated requests.
            request.Headers.TryAddWithoutValidation("Cookie", $"session_data={session}");

            using var response = await httpClient.SendAsync(request, cancellationToken);

            response.EnsureSuccessStatusCode();

            var document = await Parser.ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(cancellationToken), cancellationToken);

            var download = document.QuerySelectorAll("a[href]").FirstOrDefault()?.GetAttribute("href");

            if (string.IsNullOrWhiteSpace(download))
            {
                // Nothing to download usually means the video is private or gone. Declining lets
                // the ordinary fetch say so in its own words, which are better than ours.
                return ProviderOutcome.Declined;
            }

            return ProviderOutcome.Complete(new MediaResult(
                Clean(document.QuerySelector("h1")?.TextContent),
                Clean(document.QuerySelector("p")?.TextContent),
                Clean(document.QuerySelector("img")?.GetAttribute("src")),
                [
                    // One file, already carrying both tracks, so it is a single stream rather
                    // than a choice between any.
                    new MediaStream(new Uri(Clean(download)), IsAudio: false)
                    {
                        Label = "Video with audio",
                        FileExtension = ".mp4",
                        MimeType = "video/mp4"
                    }
                ]));
        }

        /// <summary>
        /// Reads the one-time token the form is submitted with, and the cookie it belongs to.
        /// </summary>
        private static async Task<(string Token, string? Session)> StartSessionAsync(
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            using var response = await httpClient.GetAsync(Service, cancellationToken);

            response.EnsureSuccessStatusCode();

            var document = await Parser.ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(cancellationToken), cancellationToken);

            var session = response.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? cookies.FirstOrDefault()?.Split(';').FirstOrDefault()?.Split('=').Last()
                : null;

            return (document.QuerySelector("input[name='token']")?.GetAttribute("value") ?? string.Empty, session);
        }

        /// <summary>
        /// Page text, scrubbed. Takes null because the selectors feeding it return null whenever
        /// the element is absent, which is a normal outcome rather than a fault.
        /// </summary>
        internal static string Clean(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            input = WebUtility.HtmlDecode(input);
            input = Regex.Replace(input, "<.*?>", string.Empty);
            input = input.Replace("\\\"", "\"").Replace("\"", string.Empty).Replace("\\/", "/");
            input = input.Replace("\\r", string.Empty).Replace("\\n", string.Empty);
            input = Regex.Replace(input, @"\\[\""/]", string.Empty);
            input = Regex.Replace(input, @"\s+", " ").Trim();

            // The button's own label runs into the title on this page.
            if (input.Contains("Download without Watermark"))
                input = input.Split("Download without Watermark")[0];

            return input.Trim();
        }
    }
}
