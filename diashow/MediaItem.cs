namespace diashow;

public enum MediaKind
{
    Image,
    Video
}

public sealed record MediaItem(string Path, MediaKind Kind);
