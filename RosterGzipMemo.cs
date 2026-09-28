using System;
using System.Collections.Generic;

namespace ClaudeBuddy
{
    // The last few rosters this Buddy compressed, kept so an identical one is not
    // compressed again.
    //
    // CB-216's "unchanged" answer only helps a peer that sends the roster hash
    // back, which is a peer on a build that knows to. Every older peer still
    // asks every ten seconds and gets the roster in full. Its bytes are the
    // same as last time, and so is their gzip, which is where the serving
    // machine's CPU went.
    //
    // More than one slot because there is more than one roster: an asker that
    // fetches pictures by id is sent a different roster from one that is sent
    // the pictures inline, and a machine paired with one of each would evict
    // its own entry on every ask with a single slot. Four covers both shapes
    // with room for a roster that changed between one peer's ask and another's.
    //
    // Keyed by the hash of the uncompressed bytes, which the caller has already
    // computed to answer the unchanged question, so a hit costs nothing further.
    internal sealed class RosterGzipMemo
    {
        internal const int Capacity = 4;

        private readonly Func<byte[], byte[]> _gzip;
        private readonly object _gate = new();
        private readonly Dictionary<string, byte[]> _byHash = new(StringComparer.Ordinal);
        private readonly Queue<string> _age = new();

        internal RosterGzipMemo(Func<byte[], byte[]> gzip) => _gzip = gzip;

        internal byte[] For(string hash, byte[] raw)
        {
            lock (_gate)
            {
                if (_byHash.TryGetValue(hash, out var kept)) return kept;
            }

            // Compressed outside the lock, so two peers asking at once never
            // queue behind each other's gzip. At worst both compress the same
            // new roster once and the second store is skipped, since the bytes
            // are the same.
            var gzipped = _gzip(raw);

            lock (_gate)
            {
                if (_byHash.TryAdd(hash, gzipped))
                {
                    _age.Enqueue(hash);
                    while (_byHash.Count > Capacity) _byHash.Remove(_age.Dequeue());
                }
            }

            return gzipped;
        }
    }
}
