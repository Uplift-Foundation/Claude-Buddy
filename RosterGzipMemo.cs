using System;

namespace ClaudeBuddy
{
    // The last roster this Buddy compressed, kept so an identical one is not
    // compressed again.
    //
    // CB-216's "unchanged" answer only helps a peer that sends the roster hash
    // back, which is a peer on a build that knows to. Every older peer still
    // asks every ten seconds and gets the roster in full. Its bytes are the
    // same as last time, and so is their gzip, which is where the serving
    // machine's CPU went. One slot shared by every peer is enough: they are all
    // asking about the same machine, so they are all sent the same roster.
    //
    // Keyed by the hash of the uncompressed bytes, which the caller has already
    // computed to answer the unchanged question, so a hit costs nothing further.
    internal sealed class RosterGzipMemo
    {
        private readonly Func<byte[], byte[]> _gzip;
        private readonly object _gate = new();
        private string? _hash;
        private byte[]? _gzipped;

        internal RosterGzipMemo(Func<byte[], byte[]> gzip) => _gzip = gzip;

        internal byte[] For(string hash, byte[] raw)
        {
            lock (_gate)
            {
                if (_gzipped is not null && string.Equals(_hash, hash, StringComparison.Ordinal))
                    return _gzipped;
            }

            // Compressed outside the lock, so two peers asking at once never
            // queue behind each other's gzip. At worst both compress the same
            // new roster once and the second store wins, which is the same
            // bytes.
            var gzipped = _gzip(raw);

            lock (_gate)
            {
                _hash = hash;
                _gzipped = gzipped;
            }

            return gzipped;
        }
    }
}
