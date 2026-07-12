namespace Styloagent.Core.Channel;

/// <summary>
/// Copies a channel directory tree to a fresh working location, so a live, in-use channel (agents
/// actively reading/writing its inbox/outbox) is never opened or written to directly. Styloagent seeds
/// the roster, delivers messages and writes hook/hydration state into whatever channel it opens — all of
/// which would corrupt a channel another fleet is using. Always open a snapshot, never the original.
/// </summary>
public static class ChannelSnapshot
{
    /// <summary>
    /// Recursively copies <paramref name="sourceChannel"/> into <paramref name="destChannel"/> (created if
    /// absent) and returns the destination. The source is only read. Paths are matched relatively, so a
    /// source path that recurs elsewhere in the tree can't misdirect a copy.
    /// </summary>
    public static string CopyTo(string sourceChannel, string destChannel)
    {
        Directory.CreateDirectory(destChannel);
        foreach (var file in Directory.EnumerateFiles(sourceChannel, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceChannel, file);
            var target = Path.Combine(destChannel, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        return destChannel;
    }
}
