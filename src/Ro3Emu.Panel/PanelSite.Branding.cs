using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// White-label branding (Ro3Emu:Panel:Branding): the server's own name, tagline, logo, footer and accent colour in the
/// header, the page title, the home page and the footer. Every value reaches the page as text; the colour only when it
/// is a plain #rrggbb, the logo only from the file the backend resolved inside the data folder.
/// </summary>
public sealed partial class PanelSite
{
    private const string LogoPath = "/brand/logo";

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();

    private PanelBranding Brand => _options.Branding;

    /// <summary>The name pages show: the branding's, else the server's own (Ro3Emu:ClusterName).</summary>
    private string ServerName => Trimmed(Brand.ServerName, 64) ?? _backend.Info.Name;

    /// <summary>What the pages need to brand themselves (in /api/session).</summary>
    private object BrandBody()
    {
        string? logo = _backend.BrandLogoFile is { } file && File.Exists(file)
            ? LogoPath + "?v=" + File.GetLastWriteTimeUtc(file).Ticks.ToString("x", CultureInfo.InvariantCulture) : null;
        return new
        {
            name = ServerName,
            area = _backend.Info.Area,
            tagline = Trimmed(Brand.Tagline, 160),
            footer = Trimmed(Brand.FooterText, 300),
            accent = Brand.AccentColor is { } colour && HexColour().IsMatch(colour) ? colour : null,
            logo,
        };
    }

    private static string? Trimmed(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim() is var t && t.Length > max ? t[..max] : text.Trim();

    /// <summary>The configured logo (png, jpg or webp), cached for a year behind its ?v=; 404 without one.</summary>
    private static async Task SendLogoAsync(HttpContext context, string? file)
    {
        var response = context.Response;
        string type = Path.GetExtension(file ?? "").ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "",
        };
        if (file == null || type.Length == 0 || !File.Exists(file))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        response.Headers.CacheControl = context.Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "no-cache";
        response.ContentType = type;
        response.ContentLength = new FileInfo(file).Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await response.SendFileAsync(file, context.RequestAborted);
    }
}
