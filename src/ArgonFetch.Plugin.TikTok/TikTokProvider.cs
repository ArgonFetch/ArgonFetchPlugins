using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ArgonFetch.Abstractions;

[assembly: ArgonFetchPlugin("tiktok", ArgonFetchPluginAttribute.CurrentAbi, Name = "TikTok")]

namespace ArgonFetch.Plugin.TikTok
{
    // One file already carrying both picture and sound, so yt-dlp is not run.
    public sealed class TikTokProvider : ISourceProvider
    {
        private const string Service = "https://tmate.cc";

        private static readonly HtmlParser Parser = new();

        public string Id => "tiktok";

        public IReadOnlyList<string> UrlPatterns =>
        [
            @"^https?://([\w-]+\.)*tiktok\.com/",
        ];

        public async Task<ProviderOutcome> PrepareAsync(Uri url, IProviderContext context, CancellationToken cancellationToken)
        {
            // The token is issued against a session cookie; another address is refused.
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

            request.Headers.TryAddWithoutValidation("Cookie", $"session_data={session}");

            using var response = await httpClient.SendAsync(request, cancellationToken);

            response.EnsureSuccessStatusCode();

            var document = await Parser.ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(cancellationToken), cancellationToken);

            var download = document.QuerySelectorAll("a[href]").FirstOrDefault()?.GetAttribute("href");

            if (string.IsNullOrWhiteSpace(download))
            {
                return ProviderOutcome.Declined;
            }

            return ProviderOutcome.Complete(new MediaResult(
                Clean(document.QuerySelector("h1")?.TextContent),
                Clean(document.QuerySelector("p")?.TextContent),
                Clean(document.QuerySelector("img")?.GetAttribute("src")),
                [
                    new MediaStream(new Uri(Clean(download)), IsAudio: false)
                    {
                        Label = "Video with audio",
                        FileExtension = ".mp4",
                        MimeType = "video/mp4"
                    }
                ]));
        }

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

            if (input.Contains("Download without Watermark"))
                input = input.Split("Download without Watermark")[0];

            return input.Trim();
        }
    }
}
