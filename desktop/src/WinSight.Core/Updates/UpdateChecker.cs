using System.Net;
using System.Text.Json;

namespace WinSight.Core.Updates;

public sealed record UpdateCheckResult(bool Checked, bool UpdateAvailable, string CurrentVersion, string? LatestVersion, string? DownloadUrl, string Message);

/// <summary>
/// Manual, opt-in update check. It runs only when the user presses the button and has configured an HTTPS feed; it never
/// downloads or installs anything and sends no identifying data (a plain GET). The feed is a small JSON document:
/// <c>{"version":"0.2.0","url":"https://example.org/WinSight-0.2.0-setup.exe"}</c>.
/// </summary>
public static class UpdateChecker
{
    private const int MaxFeedBytes = 64 * 1024;

    public static async Task<UpdateCheckResult> CheckAsync(string? feedUrl, string currentVersion, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        UpdateCheckResult Fail(string message) => new(false, false, currentVersion, null, null, message);
        if (string.IsNullOrWhiteSpace(feedUrl)) return Fail("Update checking is off: no update feed URL is configured.");
        if (!IsHttpsUrl(feedUrl)) return Fail("The update feed must be an absolute https:// URL.");
        if (!SemanticVersion.TryParse(currentVersion, out var current)) return Fail("The installed version could not be read: " + currentVersion);
        try
        {
            using var owned = handler is null ? new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 } : null;
            using var client = new HttpClient(handler ?? owned!, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) return Fail("The update feed redirected to a non-https address; refused.");
            if (response.StatusCode != HttpStatusCode.OK) return Fail($"The update feed answered {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaxFeedBytes) return Fail("The update feed is larger than 64 KiB; refused.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxFeedBytes) return Fail("The update feed is larger than 64 KiB; refused.");
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            var latestText = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
            var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            if (!SemanticVersion.TryParse(latestText, out var latest)) return Fail("The update feed has no valid version.");
            if (url is not null && !IsHttpsUrl(url)) return Fail("The update feed points to a non-https download; refused.");
            var available = SemanticVersion.Compare(latest, current) > 0;
            return new(true, available, currentVersion, latestText, url,
                available ? $"Version {latestText} is available (installed {currentVersion})." : $"WinSight {currentVersion} is up to date.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or IOException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            return Fail("The update feed could not be read: " + error.GetType().Name + ".");
        }
    }

    public static bool IsHttpsUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host);
}

/// <summary>Minimal SemVer 2 precedence: numeric core, a release outranks its prerelease, prerelease identifiers compare piecewise.</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string[] Prerelease)
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim().TrimStart('v', 'V');
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];
        var dash = value.IndexOf('-');
        var core = dash >= 0 ? value[..dash] : value;
        var pre = dash >= 0 ? value[(dash + 1)..].Split('.') : [];
        var parts = core.Split('.');
        if (parts.Length != 3 || pre.Any(p => p.Length == 0)) return false;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) || !int.TryParse(parts[2], out var patch)) return false;
        if (major < 0 || minor < 0 || patch < 0) return false;
        version = new SemanticVersion(major, minor, patch, pre);
        return true;
    }

    public static int Compare(SemanticVersion left, SemanticVersion right)
    {
        int core = left.Major != right.Major ? left.Major.CompareTo(right.Major)
            : left.Minor != right.Minor ? left.Minor.CompareTo(right.Minor) : left.Patch.CompareTo(right.Patch);
        if (core != 0) return core;
        if (left.Prerelease.Length == 0 || right.Prerelease.Length == 0) return right.Prerelease.Length.CompareTo(left.Prerelease.Length);
        for (int i = 0; i < Math.Min(left.Prerelease.Length, right.Prerelease.Length); i++)
        {
            var a = left.Prerelease[i]; var b = right.Prerelease[i];
            bool numericA = int.TryParse(a, out var x), numericB = int.TryParse(b, out var y);
            int order = numericA && numericB ? x.CompareTo(y) : numericA ? -1 : numericB ? 1 : string.CompareOrdinal(a, b);
            if (order != 0) return order;
        }
        return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
    }
}
