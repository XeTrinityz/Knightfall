#nullable enable

using System.Net.Http.Headers;
using System.Text.Json;

namespace Knightfall;

// Asks GitHub once per game session whether Knightfall or BmSDK-AK has a newer release, and says so. Nothing
// is downloaded or installed. The player updates by hand.
static class Updates
{
    const string ModRepo = "XeTrinityz/Knightfall";
    const string ModPage = "https://www.nexusmods.com/batmanarkhamknight/mods/3660";
    const string SdkRepo = "XeTrinityz/BmSDK-AK";

    sealed record Found(string Version, string Link);

    static readonly HttpClient Web = Made();

    static Task<(Found? Mod, Found? Sdk, string? Notes, bool Reached)>? _asking;
    static bool _forced;
    static bool _forcedReport;

    public static readonly Toggle Check = new(
        "settings.general.check_updates", "CHECK FOR UPDATES",
        "Asks GitHub when the game starts if there is a newer Knightfall or BmSDK-AK and tells you. Nothing is downloaded. It sends no information about you or your game.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Command Now = new(
        "settings.general.check_now", "CHECK NOW",
        "Looks for a newer Knightfall or BmSDK-AK right now.",
        static () =>
        {
            if (_asking is not null)
            {
                return Outcome.Unchanged("ALREADY ASKING");
            }

            _forced = true;
            return Outcome.Asking;
        })
    {
        Cap = "CHECK",
    };

    // Only on the main menu's KNIGHTFALL screen, where the game's own box shows the text.
    public static readonly Command Changes = new(
        "settings.general.changes", "WHAT'S NEW",
        "The changelog of the latest Knightfall release. It comes from the update check, so it needs CHECK FOR UPDATES on and a connection.",
        static () => MainMenuScreen.ShowNotes())
    {
        Cap = "SHOW",
    };

    public static readonly Command CopyMod = new(
        "settings.general.copy_mod_link", "COPY KNIGHTFALL LINK",
        "Copies the Nexus page of the newer Knightfall, to paste in your browser.",
        static () => Copy(Pending(Survive.NewMod)))
    {
        Cap = "COPY",
        Block = static () => Why(Survive.NewMod),
    };

    public static readonly Command CopySdk = new(
        "settings.general.copy_sdk_link", "COPY BMSDK-AK LINK",
        "Copies the download page of the newer BmSDK-AK, to paste in your browser. Close the game before you install it.",
        static () => Copy(Pending(Survive.NewSdk)))
    {
        Cap = "COPY",
        Block = static () => Why(Survive.NewSdk),
    };

    public static void Tick()
    {
        if (_asking is { IsCompleted: true } done)
        {
            _asking = null;
            Finish(done);
            return;
        }

        if (_asking is not null || (!_forced && !Due()))
        {
            return;
        }

        var forced = _forced;
        _forced = false;
        if (!forced)
        {
            Survive.Set(Survive.UpdateAsked, true);
        }

        var mine = Read(Kernel.Version);
        var sdk = Read(typeof(Script).Assembly.GetName().Version?.ToString());
        _asking = Task.Run(async () => await Ask(mine, sdk).ConfigureAwait(false));
        _forcedReport = forced;
    }

    static bool Due() => Check.On && !Survive.Get(Survive.UpdateAsked, false) && Read(Kernel.Version) is not null;

    static void Finish(Task<(Found? Mod, Found? Sdk, string? Notes, bool Reached)> done)
    {
        var report = _forcedReport;
        _forcedReport = false;

        if (done.IsFaulted || done.IsCanceled)
        {
            Log.Info("The update check failed: " + (done.Exception?.GetBaseException().Message ?? "cancelled") + ".");
            if (report)
            {
                Now.Report(Outcome.Failed("NO REPLY"));
            }

            return;
        }

        var (mod, sdk, notes, reached) = done.Result;
        if (!reached)
        {
            Log.Info("The update check could not reach GitHub.");
            if (report)
            {
                Now.Report(Outcome.Failed("NO REPLY"));
            }

            return;
        }

        Survive.Set(Survive.NewMod, mod is null ? "" : mod.Version + "|" + mod.Link);
        Survive.Set(Survive.NewSdk, sdk is null ? "" : sdk.Version + "|" + sdk.Link);
        if (notes is not null)
        {
            Survive.Set(Survive.ModNotes, notes);
        }

        if (mod is not null)
        {
            Toasts.Show("KNIGHTFALL " + mod.Version, "NEW VERSION, SEE SETTINGS > GENERAL", OutcomeKind.Done, always: true);
            Log.Info($"Knightfall {mod.Version} is out. This is {Kernel.Version}. {mod.Link}");
        }

        if (sdk is not null)
        {
            Toasts.Show("BMSDK-AK " + sdk.Version, "NEW VERSION, SEE SETTINGS > GENERAL", OutcomeKind.Done, always: true);
            Log.Info($"BmSDK-AK {sdk.Version} is out. {sdk.Link}");
        }

        if (report)
        {
            Now.Report(mod is null && sdk is null ? Outcome.Unchanged("UP TO DATE") : Outcome.Done("NEWER FOUND"));
        }
    }

    // A repo with no release yet answers 404, which just means nothing newer.
    static async Task<(Found? Mod, Found? Sdk, string? Notes, bool Reached)> Ask(Version? mine, Version? sdk)
    {
        var (mod, notes, modReached) = await Latest(ModRepo, mine).ConfigureAwait(false);
        var (lib, _, libReached) = await Latest(SdkRepo, sdk).ConfigureAwait(false);
        return (mod, lib, notes, modReached || libReached);
    }

    // Notes are the release's text, with its version on the first line.
    static async Task<(Found? Newer, string? Notes, bool Reached)> Latest(string repo, Version? have)
    {
        try
        {
            using var response = await Web.GetAsync($"https://api.github.com/repos/{repo}/releases/latest").ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (null, null, true);
            }

            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var tag = json.RootElement.GetProperty("tag_name").GetString();
            var link = json.RootElement.GetProperty("html_url").GetString();
            var body = json.RootElement.TryGetProperty("body", out var text) ? text.GetString() : null;
            var found = Read(tag);
            var notes = found is null || string.IsNullOrWhiteSpace(body) ? null : found + "\n" + body;
            if (have is null || found is null || link is null || found <= have)
            {
                return (null, notes, true);
            }

            return (new Found(found.ToString(), repo == ModRepo ? ModPage : link), notes, true);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, null, false);
        }
    }

    // 1.9.0, v1.9 or Knightfall 1.9.0, as three numbers so 0.24.1 equals 0.24.1.0.
    static Version? Read(string? text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text ?? "", @"\d+(\.\d+){1,3}");
        if (!match.Success || !System.Version.TryParse(match.Value, out var parsed))
        {
            return null;
        }

        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
    }

    // The newest Knightfall release's changelog as plain lines, or null when there is none yet.
    // Markdown is dropped, since the game's text boxes show it as it is.
    public static string? Notes()
    {
        var text = Survive.Get(Survive.ModNotes, "");
        var first = text.IndexOf('\n');
        if (first <= 0)
        {
            return null;
        }

        var lines = new List<string> { "KNIGHTFALL " + text[..first] };
        foreach (var raw in text[(first + 1)..].Split('\n'))
        {
            var line = raw.Trim().Replace("**", "").Replace("`", "");
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                lines.Add("");
                lines.Add(line.TrimStart('#', ' ').ToUpperInvariant());
            }
            else
            {
                lines.Add(line.StartsWith("* ", StringComparison.Ordinal) ? "- " + line[2..] : line);
            }
        }

        return lines.Count > 1 ? string.Join("\n", lines) : null;
    }

    static string? Pending(string key)
    {
        var text = Survive.Get(key, "");
        var bar = text.IndexOf('|');
        return bar > 0 ? text[(bar + 1)..] : null;
    }

    static string? Why(string key)
    {
        if (Pending(key) is not null)
        {
            return null;
        }

        return Survive.Get<string?>(key, null) is null ? "NOT CHECKED YET" : "UP TO DATE";
    }

    static Outcome Copy(string? link)
    {
        if (link is null)
        {
            return Outcome.Unchanged("NOTHING NEWER");
        }

        return Clipboard.Put(link) ? Outcome.Done("COPIED") : Outcome.Failed("CLIPBOARD BUSY");
    }

    static HttpClient Made()
    {
        var web = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        web.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("update-check", "1"));
        web.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return web;
    }
}
