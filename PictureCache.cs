using System;
using System.Collections.Generic;

namespace ClaudeBuddy
{
    // The persona pictures a mirror client has fetched, by id (CB-216).
    //
    // Keyed by id, which is the picture's hash and length, so two peers offering
    // the same file share one entry, and the bytes under an id never change.
    // Bounded because the pictures are large (11-15 MB GIFs on the machine this
    // was measured on); the oldest goes first, and forgetting one costs a single
    // fetch the next time a roster names it.
    internal sealed class PictureCache
    {
        private readonly int _capacity;
        private readonly Dictionary<string, byte[]> _byId = new(StringComparer.Ordinal);
        private readonly Queue<string> _age = new();

        internal PictureCache(int capacity) => _capacity = capacity;

        internal int Count => _byId.Count;

        internal byte[]? Get(string id) => _byId.GetValueOrDefault(id);

        internal void Add(string id, byte[] bytes)
        {
            if (!_byId.TryAdd(id, bytes)) return;

            _age.Enqueue(id);
            while (_byId.Count > _capacity) _byId.Remove(_age.Dequeue());
        }
    }
}
