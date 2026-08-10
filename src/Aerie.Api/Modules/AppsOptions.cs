namespace Aerie.Api.Modules;

/// <summary>
/// Deploy-time config shared by the family shell's apps (the "Apps" appsettings
/// section). Registered in <see cref="ModuleRegistration.AddAerieModules"/>
/// rather than Program.cs, so it stays part of the module seam.
/// </summary>
public class AppsOptions
{
    public const string SectionName = "Apps";

    /// <summary>
    /// Absolute base URL of this Aerie install as it should be *printed* -
    /// e.g. "https://home.example.com". Empty by default and supplied at
    /// deploy (compose.prod.yml passes <c>Apps__PublicBaseUrl</c> from the
    /// DOMAIN operator variable); deliberately never a literal in the repo,
    /// per docs/ethos.md.
    ///
    /// It exists because a QR label is not a link in a page: it's taped to a
    /// box for a decade, and it has to carry the canonical host rather than
    /// whichever hostname or IP the laptop that printed the sheet happened to
    /// be using. Everything else in the shell is same-origin and needs no
    /// base URL at all.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>
    /// The value as a client should use it, or null if it is unset or unusable.
    /// Trailing slashes go (callers append rooted paths), and anything that
    /// isn't an absolute http(s) URL is rejected rather than passed on: a bare
    /// "home.example.com" printed into a QR gives a camera nothing to open, and
    /// that failure would only show up on paper.
    /// </summary>
    public static string? NormalizeBaseUrl(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed)) return null;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? trimmed
                : null;
    }
}
