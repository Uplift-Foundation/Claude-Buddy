namespace Orbweaver
{
    internal enum PeerInboundWriteKind { Written, Unchanged, Refused, Failed }

    internal readonly record struct PeerInboundWrite(PeerInboundWriteKind Kind, string? Reason);

    // CB-267 (stub): reads/writes <root>/settings.json's crossSessionInbound.
    internal static class PeerInboundSettingsFile
    {
        internal const string BackupSuffix = ".orbweaver-backup";

        internal static string PathFor(string root) => Path.Combine(root, "settings.json");
        internal static string? ReadText(string root) => throw new NotImplementedException();
        internal static PeerInboundWrite Apply(string root, bool accept) => throw new NotImplementedException();
    }
}
