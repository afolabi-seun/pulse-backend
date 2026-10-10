namespace Pulse.Domain.Organizations;

/// <summary>
/// An organization's logo image. Kept in its own table so the bytes are never loaded with ordinary
/// organization lookups. Only raster formats: an SVG can carry script.
/// </summary>
public class OrganizationLogo
{
    public const int MaxBytes = 256 * 1024;
    public static readonly IReadOnlyList<string> AllowedContentTypes = ["image/png", "image/jpeg", "image/webp"];

    public Guid OrganizationId { get; private set; }
    public string ContentType { get; private set; } = string.Empty;
    public byte[] Data { get; private set; } = [];

    private OrganizationLogo() { }

    public static OrganizationLogo Create(Guid organizationId, string contentType, byte[] data)
    {
        var logo = new OrganizationLogo { OrganizationId = organizationId };
        logo.Replace(contentType, data);
        return logo;
    }

    public void Replace(string contentType, byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxBytes)
            throw new ArgumentException($"A logo must be between 1 byte and {MaxBytes / 1024} KB.", nameof(data));
        if (DetectContentType(data) is not { } detected || detected != contentType)
            throw new ArgumentException("A logo must be a PNG, JPEG or WebP image.", nameof(contentType));
        ContentType = contentType;
        Data = data;
    }

    /// <summary>The image type according to the file's own leading bytes — never the uploader's claim.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (data.Length >= 3 && data[..3].SequenceEqual<byte>([0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
