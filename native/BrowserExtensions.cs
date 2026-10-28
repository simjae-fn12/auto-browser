using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PluckWindowsNative;

// Chrome extensions through WebView2 browser extensions. Every pluck profile is its own WebView2 profile, so each workspace
// has its own set. WebView2 loads unpacked folders and keeps reading them, so pluck keeps a copy of each version under
// dataDir\extensions\<id>\<version>. WebView2 has no toolbar, popups or Web Store install button: pluck draws the buttons
// in the sidebar, opens popups in a small window, and installs from a store link by downloading the CRX itself.
public sealed partial class BrowserForm
{
    private sealed record ExtensionInfo(string Id, string Name, string Version, string? Icon, string? Popup, string? Options, bool Enabled);

    private string ExtensionsDir => Path.Combine(dataDir, "extensions");
    private readonly Dictionary<string, ExtensionInfo[]> extensionCache = [];
    private string toolbarExtensionsKey = "";
    private static readonly Regex ExtensionIdPattern = new("(?<![a-z])([a-p]{32})(?![a-z])");
    private static readonly HttpClient storeClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    // Extensions are installed into a WebView2 profile object, which only exists through a WebView in that profile.
    // A profile without an open tab gets a hidden controller for the duration of the call.
    private async Task<T> WithProfileAsync<T>(string profile, Func<CoreWebView2Profile, Task<T>> work)
    {
        var open = browserTabs.Values.FirstOrDefault(tab => tab.Profile == profile && tab.View.CoreWebView2 != null)?.View.CoreWebView2
            ?? (profile == DefaultProfile ? chromeView.CoreWebView2 : null);
        if (open != null) return await work(open.Profile);
        if (environment == null) throw new InvalidOperationException("Browser is not ready");
        var controller = ControllerOptions(profile) is { } options
            ? await environment.CreateCoreWebView2ControllerAsync(Handle, options)
            : await environment.CreateCoreWebView2ControllerAsync(Handle);
        try { controller.IsVisible = false; return await work(controller.CoreWebView2.Profile); }
        finally { controller.Close(); }
    }

    private static JsonObject? ReadManifest(string folder)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
            }) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string? ManifestText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // "__MSG_appName__" names come from _locales, Korean first.
    private static string LocalizedName(string folder, JsonObject manifest)
    {
        var name = ManifestText(manifest["name"]) ?? "";
        if (!name.StartsWith("__MSG_", StringComparison.Ordinal) || !name.EndsWith("__", StringComparison.Ordinal)) return name;
        var key = name[6..^2];
        foreach (var locale in new[] { "ko", ManifestText(manifest["default_locale"]) ?? "en", "en" })
        {
            try
            {
                var file = Path.Combine(folder, "_locales", locale, "messages.json");
                if (!File.Exists(file)) continue;
                var messages = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                var entry = messages?.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
                if (ManifestText(entry?["message"]) is { Length: > 0 } message) return message;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return name;
    }

    private static string? ExtensionIcon(string folder, JsonObject manifest)
    {
        var action = manifest["action"] ?? manifest["browser_action"];
        var candidates = new List<(int Size, string Path)>();
        void Collect(JsonNode? node)
        {
            if (ManifestText(node) is { } single) candidates.Add((32, single));
            else if (node is JsonObject sizes)
                foreach (var (size, path) in sizes)
                    if (int.TryParse(size, out var px) && ManifestText(path) is { } file) candidates.Add((px, file));
        }
        Collect(action?["default_icon"]);
        if (candidates.Count == 0) Collect(manifest["icons"]);
        // The smallest icon that is still sharp at 16px on a 200% display.
        var pick = candidates.OrderBy(c => c.Size >= 32 ? 0 : 1).ThenBy(c => c.Size >= 32 ? c.Size : -c.Size).Select(c => c.Path).FirstOrDefault();
        if (pick == null) return null;
        var path = Path.GetFullPath(Path.Combine(folder, pick.TrimStart('/')));
        if (!path.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        var type = Path.GetExtension(path).ToLowerInvariant() switch { ".svg" => "image/svg+xml", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
        var bytes = File.ReadAllBytes(path);
        return bytes.Length > 200_000 ? null : $"data:{type};base64,{Convert.ToBase64String(bytes)}";
    }

    private string? ExtensionFolder(string id)
    {
        var root = Path.Combine(ExtensionsDir, id);
        return Directory.Exists(root) ? Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault() : null;
    }

    private async Task<ExtensionInfo[]> ListExtensionsAsync(string profile)
    {
        var installed = await WithProfileAsync(profile, async p => (await p.GetBrowserExtensionsAsync()).ToArray());
        // WebView2's own built-in extensions (Edge PDF viewer, clipboard) are not the person's and stay out of the list.
        var items = installed.Where(extension => ExtensionFolder(extension.Id) != null).Select(extension =>
        {
            var folder = ExtensionFolder(extension.Id);
            var manifest = folder == null ? null : ReadManifest(folder);
            if (folder == null || manifest == null) return new ExtensionInfo(extension.Id, extension.Name, "", null, null, null, extension.IsEnabled);
            var action = manifest["action"] ?? manifest["browser_action"];
            return new ExtensionInfo(extension.Id, LocalizedName(folder, manifest), ManifestText(manifest["version"]) ?? "", ExtensionIcon(folder, manifest),
                ManifestText(action?["default_popup"]), ManifestText(manifest["options_ui"]?["page"]) ?? ManifestText(manifest["options_page"]), extension.IsEnabled);
        }).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        extensionCache[profile] = items;
        return items;
    }

    // The sidebar shows the extensions of the profile the active tab belongs to.
    private async Task PostToolbarExtensionsAsync(bool reload = false)
    {
        if (!chromeReady || environment == null) return;
        var profile = Active()?.Profile ?? userProfile;
        if (reload || !extensionCache.ContainsKey(profile))
        {
            try { await ListExtensionsAsync(profile); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return; }
        }
        var items = extensionCache.GetValueOrDefault(profile) ?? [];
        var key = profile + "|" + string.Join(",", items.Select(e => e.Id + e.Enabled + e.Version));
        if (!reload && key == toolbarExtensionsKey) return;
        toolbarExtensionsKey = key;
        PostChrome(new { type = "extensions", profile, items = items.Where(e => e.Enabled).Select(e => new { id = e.Id, name = e.Name, icon = e.Icon }).ToArray() });
    }

    private async Task PostExtensionSettingsAsync(string profile)
    {
        ExtensionInfo[] items;
        try { items = await ListExtensionsAsync(profile); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { items = []; }
        var owner = FindProfile(profile);
        PostChrome(new
        {
            type = "extensions.settings", profile,
            items = items.Select(e => new { id = e.Id, name = e.Name, version = e.Version, icon = e.Icon, enabled = e.Enabled, options = e.Options != null }).ToArray(),
            sources = FindBrowserProfiles().Select(p => new { source = p.Source, label = $"{p.Browser.Label} · {(p.Name.Length > 0 ? p.Name : p.Folder)}{(p.Email.Length > 0 ? $" ({p.Email})" : "")}" }).ToArray(),
            source = owner?.Source ?? ""
        });
        if (profile == (Active()?.Profile ?? userProfile)) await PostToolbarExtensionsAsync(true);
    }

    // Copies an unpacked extension into pluck's store and installs it, replacing an older version in that profile.
    private async Task<string> InstallExtensionFolderAsync(string profile, string source, string? expectedId)
    {
        var manifest = ReadManifest(source) ?? throw new InvalidDataException("manifest.json이 없습니다.");
        if (manifest.ContainsKey("theme")) throw new InvalidDataException("테마는 설치할 수 없습니다.");
        if (manifest.ContainsKey("app")) throw new InvalidDataException("크롬 앱은 설치할 수 없습니다.");
        var id = expectedId ?? (ManifestText(manifest["key"]) is { } key ? ExtensionId(Convert.FromBase64String(key)) : null)
            ?? throw new InvalidDataException("확장 ID를 알 수 없습니다.");
        var version = Regex.Replace(ManifestText(manifest["version"]) ?? "0", "[^0-9A-Za-z._-]", "_");
        var target = Path.Combine(ExtensionsDir, id, version);
        if (!Directory.Exists(target))
        {
            var staging = target + ".tmp";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            CopyDirectory(source, staging);
            // Chrome refuses to load an unpacked extension that has folders starting with "_" other than _locales.
            foreach (var reserved in Directory.GetDirectories(staging).Where(d => Path.GetFileName(d).StartsWith('_') && Path.GetFileName(d) != "_locales"))
                Directory.Delete(reserved, true);
            Directory.Move(staging, target);
        }
        Directory.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        var name = await WithProfileAsync(profile, async p =>
        {
            foreach (var old in (await p.GetBrowserExtensionsAsync()).Where(e => e.Id == id)) await old.RemoveAsync();
            return (await p.AddBrowserExtensionAsync(target)).Name;
        });
        return string.IsNullOrEmpty(name) ? LocalizedName(target, manifest) : name;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var folder in Directory.GetDirectories(source)) CopyDirectory(folder, Path.Combine(target, Path.GetFileName(folder)));
    }

    // Chrome derives an extension id from its public key: the first 16 bytes of SHA-256, written with letters a-p.
    private static string ExtensionId(byte[] publicKey) =>
        string.Concat(SHA256.HashData(publicKey).Take(16).SelectMany(b => new[] { (char)('a' + (b >> 4)), (char)('a' + (b & 15)) }));

    private static string? StoreExtensionId(string text) => ExtensionIdPattern.Match(text.Trim().ToLowerInvariant()) is { Success: true } match ? match.Groups[1].Value : null;

    private async Task<string> InstallFromStoreAsync(string profile, string text)
    {
        var id = StoreExtensionId(text) ?? throw new InvalidDataException("크롬 웹 스토어 주소나 확장 ID(영문 32자)를 넣어 주세요.");
        var edge = text.Contains("microsoftedge.microsoft.com", StringComparison.OrdinalIgnoreCase);
        var version = environment?.BrowserVersionString.Split(' ')[0] ?? "130.0.0.0";
        var url = edge
            ? $"https://edge.microsoft.com/extensionwebstorebase/v1/crx?response=redirect&x=id%3D{id}%26installsource%3Dondemand%26uc"
            : $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion={version}&acceptformat=crx2,crx3&x=id%3D{id}%26installsource%3Dondemand%26uc";
        using var response = await storeClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"스토어에서 받지 못했습니다 (HTTP {(int)response.StatusCode}).");
        var crx = await response.Content.ReadAsByteArrayAsync();
        if (crx.Length == 0) throw new InvalidDataException("스토어에 없는 확장이거나 이 브라우저에서 받을 수 없는 확장입니다.");
        var (zip, publicKey) = ReadCrx(crx, id);
        var staging = Path.Combine(ExtensionsDir, ".download-" + id);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        try
        {
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read)) archive.ExtractToDirectory(staging);
            // The key keeps the store id, which extensions and their sign-in pages rely on.
            if (publicKey != null && ReadManifest(staging) is { } manifest && !manifest.ContainsKey("key"))
            {
                manifest["key"] = Convert.ToBase64String(publicKey);
                File.WriteAllText(Path.Combine(staging, "manifest.json"), manifest.ToJsonString());
            }
            return await InstallExtensionFolderAsync(profile, staging, id);
        }
        finally { try { Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    // CRX2: magic, version, key length, signature length, key, signature, zip. CRX3: magic, version, header length,
    // a protobuf CrxFileHeader whose AsymmetricKeyProof entries (fields 2 and 3) carry public keys, then the zip.
    private static (byte[] Zip, byte[]? PublicKey) ReadCrx(byte[] crx, string id)
    {
        if (crx.Length < 16 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4')
            return crx.Length > 4 && crx[0] == 'P' && crx[1] == 'K' ? (crx, null) : throw new InvalidDataException("확장 파일 형식이 아닙니다.");
        var version = BitConverter.ToInt32(crx, 4);
        if (version == 2)
        {
            var keyLength = BitConverter.ToInt32(crx, 8);
            var signatureLength = BitConverter.ToInt32(crx, 12);
            return (crx[(16 + keyLength + signatureLength)..], crx[16..(16 + keyLength)]);
        }
        var headerLength = BitConverter.ToInt32(crx, 8);
        var header = crx[12..(12 + headerLength)];
        var keys = new List<byte[]>();
        foreach (var (field, proof) in ProtoFields(header))
            if (field is 2 or 3)
                keys.AddRange(ProtoFields(proof).Where(f => f.Field == 1).Select(f => f.Value));
        return (crx[(12 + headerLength)..], keys.FirstOrDefault(key => ExtensionId(key) == id));
    }

    // Length-delimited fields only; CrxFileHeader has nothing else at the levels read here.
    private static IEnumerable<(int Field, byte[] Value)> ProtoFields(byte[] data)
    {
        var position = 0;
        ulong Varint()
        {
            ulong result = 0;
            for (var shift = 0; position < data.Length; shift += 7)
            {
                var b = data[position++];
                result |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) break;
            }
            return result;
        }
        while (position < data.Length)
        {
            var tag = Varint();
            var wire = (int)(tag & 7);
            if (wire == 2)
            {
                var length = (int)Varint();
                if (length < 0 || position + length > data.Length) yield break;
                yield return ((int)(tag >> 3), data[position..(position + length)]);
                position += length;
            }
            else if (wire == 0) Varint();
            else if (wire == 1) position += 8;
            else if (wire == 5) position += 4;
            else yield break;
        }
    }

    // Brings over the extensions a Chrome/Edge profile uses, as the person installed them (not built-in or default ones).
    private async Task<(int Added, List<string> Failed)> ImportBrowserExtensionsAsync(string profile, string source)
    {
        var found = FindBrowserProfiles().FirstOrDefault(p => p.Source == source) ?? throw new InvalidDataException("가져올 브라우저 프로필을 찾지 못했습니다.");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), found.Browser.DataDir, found.Folder);
        var settings = new Dictionary<string, JsonNode?>();
        foreach (var file in new[] { "Secure Preferences", "Preferences" })
        {
            try
            {
                using var stream = new FileStream(Path.Combine(root, file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (JsonNode.Parse(stream)?["extensions"]?["settings"] is JsonObject entries)
                    foreach (var (id, entry) in entries) settings.TryAdd(id, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        int added = 0;
        var failed = new List<string>();
        var extensions = Path.Combine(root, "Extensions");
        if (!Directory.Exists(extensions)) return (0, failed);
        foreach (var folder in Directory.GetDirectories(extensions))
        {
            var id = Path.GetFileName(folder);
            if (!ExtensionIdPattern.IsMatch(id) || id.Length != 32) continue;
            var entry = settings.GetValueOrDefault(id);
            // Built-in (5, 10) and default-installed ones, and ones the person turned off, stay behind.
            var location = entry?["location"] is JsonValue l && l.TryGetValue<int>(out var n) ? n : 1;
            if (location is 5 or 10 || entry?["was_installed_by_default"]?.GetValue<bool>() == true
                || entry?["disable_reasons"] is JsonArray { Count: > 0 } || entry?["state"] is JsonValue s && s.TryGetValue<int>(out var state) && state == 0) continue;
            var latest = Directory.GetDirectories(folder).Where(v => File.Exists(Path.Combine(v, "manifest.json"))).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
            if (latest == null || ReadManifest(latest) is not { } manifest || manifest.ContainsKey("theme") || manifest.ContainsKey("app")) continue;
            try { await InstallExtensionFolderAsync(profile, latest, id); added++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException
                or System.Runtime.InteropServices.COMException) { failed.Add(LocalizedName(latest, manifest)); }
        }
        return (added, failed);
    }

    private async Task HandleExtensionMessageAsync(string type, JsonElement message)
    {
        var profile = FindProfile(Read(message, "profile"))?.Id ?? Active()?.Profile ?? userProfile;
        var id = Read(message, "id");
        void Note(string text) => PostChrome(new { type = "settings.note", text });
        try
        {
            switch (type)
            {
                case "ext.list": break;
                case "ext.install":
                    Note("설치하는 중…");
                    Note($"「{await InstallFromStoreAsync(profile, Read(message, "text"))}」을(를) 「{ProfileName(profile)}」에 설치했습니다.");
                    break;
                case "ext.storeInstall":
                    if (Active() is not { } shown || shown.View.Source?.ToString() is not { } page) return;
                    profile = shown.Profile;
                    PostChrome(new { type = "extensions.busy", busy = true });
                    try { PostChrome(new { type = "toast", text = $"「{await InstallFromStoreAsync(profile, page)}」을(를) 「{ProfileName(profile)}」에 추가했습니다." }); }
                    finally { PostChrome(new { type = "extensions.busy", busy = false }); }
                    await PostToolbarExtensionsAsync(true);
                    return;
                case "ext.import":
                    Note("가져오는 중…");
                    var (added, failed) = await ImportBrowserExtensionsAsync(profile, Read(message, "source"));
                    Note(added == 0 && failed.Count == 0 ? "가져올 확장 프로그램이 없습니다."
                        : $"확장 {added}개를 가져왔습니다." + (failed.Count > 0 ? $" 설치하지 못한 것: {string.Join(", ", failed)}" : ""));
                    break;
                case "ext.toggle":
                    var enabled = Read(message, "enabled") == "True";
                    await WithProfileAsync(profile, async p =>
                    {
                        foreach (var e in (await p.GetBrowserExtensionsAsync()).Where(e => e.Id == id)) await e.EnableAsync(enabled);
                        return true;
                    });
                    break;
                case "ext.remove":
                    await WithProfileAsync(profile, async p =>
                    {
                        foreach (var e in (await p.GetBrowserExtensionsAsync()).Where(e => e.Id == id)) await e.RemoveAsync();
                        return true;
                    });
                    break;
                case "ext.options":
                    if (extensionCache.GetValueOrDefault(profile)?.FirstOrDefault(e => e.Id == id)?.Options is { } options)
                    {
                        PostChrome(new { type = "settings.close" });
                        await CreateTabAsync($"chrome-extension://{id}/{options.TrimStart('/')}", "user", true, profile: profile);
                    }
                    return;
                case "ext.popup": await OpenExtensionPopupAsync(id, (int)Number(message, "x"), (int)Number(message, "y")); return;
                default: return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException or TaskCanceledException
            or ArgumentException or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Note("확장 프로그램 작업에 실패했습니다: " + ex.Message);
            if (type == "ext.storeInstall") return;
        }
        await PostExtensionSettingsAsync(profile);
    }

    private static double Number(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private Form? extensionPopup;

    // Chrome shows an extension's popup under its toolbar button and closes it when it loses focus; this does the same in
    // a borderless window next to the sidebar button (x, y: the button's corner in the chrome page).
    private async Task OpenExtensionPopupAsync(string id, int x, int y)
    {
        extensionPopup?.Close();
        var profile = Active()?.Profile ?? userProfile;
        if (extensionCache.GetValueOrDefault(profile)?.FirstOrDefault(e => e.Id == id) is not { } extension || environment == null) return;
        if (extension.Popup == null)
        {
            if (extension.Options != null) await CreateTabAsync($"chrome-extension://{id}/{extension.Options.TrimStart('/')}", "user", true, profile: profile);
            else PostChrome(new { type = "toast", text = $"「{extension.Name}」은(는) 버튼 화면 없이 페이지에서 동작합니다." });
            return;
        }
        var popup = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            BackColor = Color.FromArgb(200, 200, 200), Padding = new Padding(1),
            // Off screen until the page has its size; a layered (semi-transparent) window would not show the WebView.
            Bounds = new Rectangle(-32000, -32000, 362, 2)
        };
        var anchor = chromeView.PointToScreen(new Point(x, y));
        var view = new WebView2 { Dock = DockStyle.Fill };
        popup.Controls.Add(view);
        extensionPopup = popup;
        popup.FormClosed += (_, _) => { if (extensionPopup == popup) extensionPopup = null; };
        popup.Deactivate += (_, _) => popup.Close();
        popup.Show(this);
        if (ControllerOptions(profile) is { } options) await view.EnsureCoreWebView2Async(environment, options);
        else await view.EnsureCoreWebView2Async(environment);
        if (popup.IsDisposed) return;
        var core = view.CoreWebView2;
        core.WindowCloseRequested += (_, _) => popup.Close();
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _ = CreateTabAsync(e.Uri, "user", true, profile: profile);
            popup.Close();
        };
        var sized = false;
        async Task Fit()
        {
            if (popup.IsDisposed) return;
            try
            {
                // The popup's natural size, as Chrome measures it: limited to 800×600 and at least 25×25.
                var json = await core.ExecuteScriptAsync("(() => { const b = document.body, d = document.documentElement; if (!b) return [0, 0]; return [Math.max(b.scrollWidth, d.scrollWidth, b.offsetWidth), Math.max(b.scrollHeight, d.scrollHeight, b.offsetHeight)]; })()");
                var size = JsonSerializer.Deserialize<int[]>(json) ?? [0, 0];
                if (size[0] == 0 || popup.IsDisposed) return;
                var width = Math.Clamp(size[0], 25, 800) + 2;
                var height = Math.Clamp(size[1], 25, 600) + 2;
                var screen = Screen.FromPoint(anchor).WorkingArea;
                popup.Bounds = new Rectangle(Math.Min(anchor.X, screen.Right - width), Math.Min(anchor.Y, screen.Bottom - height), width, height);
                if (!sized) { sized = true; popup.Activate(); view.Focus(); }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or JsonException or System.Runtime.InteropServices.COMException) { }
        }
        core.NavigationCompleted += async (_, _) =>
        {
            // Popups often fill themselves in after load; follow their size for a moment like Chrome does.
            for (var i = 0; i < 12 && !popup.IsDisposed; i++) { await Fit(); await Task.Delay(i < 4 ? 60 : 250); }
        };
        core.Navigate($"chrome-extension://{id}/{extension.Popup.TrimStart('/')}");
    }
}
