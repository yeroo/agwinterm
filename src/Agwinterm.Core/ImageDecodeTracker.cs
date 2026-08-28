using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agwinterm.Core.Tests")]
[assembly: InternalsVisibleTo("Agwinterm.Win32")]

namespace Agwinterm.Core;

/// <summary>
/// Tracks the newest pane-local image objects and the bounded set of conversions currently in
/// flight. Image ids are scoped by terminal core: two split panes may both use id 1 without either
/// pane superseding the other's work.
/// </summary>
internal sealed class ImageDecodeTracker
{
    private readonly ConcurrentDictionary<OwnerImageId, KittyImage> _latest = new();
    private readonly HashSet<OwnedImage> _inFlight = [];

    /// <summary>Publish one terminal core's current images without disturbing any other core.</summary>
    public void Publish(ITerminalCore owner, IEnumerable<KittyImage> images)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(images);

        var liveIds = new HashSet<int>();
        foreach (KittyImage image in images)
        {
            liveIds.Add(image.Id);
            _latest[new OwnerImageId(owner, image.Id)] = image;
        }

        foreach (OwnerImageId key in _latest.Keys)
            if (ReferenceEquals(key.Owner, owner) && !liveIds.Contains(key.ImageId))
                _latest.TryRemove(key, out _);
    }

    /// <summary>Forget image identities belonging to terminal cores not rendered this frame.</summary>
    public void RetainOwners(IEnumerable<ITerminalCore> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        var liveOwners = new HashSet<ITerminalCore>(owners, ReferenceEqualityComparer.Instance);
        foreach (OwnerImageId key in _latest.Keys)
            if (!liveOwners.Contains(key.Owner)) _latest.TryRemove(key, out _);
    }

    public bool IsLatest(ITerminalCore owner, KittyImage image)
        => _latest.TryGetValue(new OwnerImageId(owner, image.Id), out KittyImage? latest)
           && ReferenceEquals(latest, image);

    /// <summary>
    /// Reserve one of the global conversion slots for this exact pane/image pair. Called only by
    /// the renderer thread; background workers consult <see cref="IsLatest"/> but do not mutate the
    /// in-flight set.
    /// </summary>
    public bool TryStart(ITerminalCore owner, KittyImage image, int limit)
    {
        if (limit <= 0 || _inFlight.Count >= limit || !IsLatest(owner, image)) return false;
        return _inFlight.Add(new OwnedImage(owner, image));
    }

    /// <summary>Release a conversion slot after its completion has returned to the renderer.</summary>
    public void Complete(ITerminalCore owner, KittyImage image)
        => _inFlight.Remove(new OwnedImage(owner, image));

    internal int InFlightCount => _inFlight.Count;

    private readonly struct OwnerImageId(ITerminalCore owner, int imageId) : IEquatable<OwnerImageId>
    {
        public ITerminalCore Owner { get; } = owner;
        public int ImageId { get; } = imageId;

        public bool Equals(OwnerImageId other)
            => ReferenceEquals(Owner, other.Owner) && ImageId == other.ImageId;

        public override bool Equals(object? obj) => obj is OwnerImageId other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Owner), ImageId);
    }

    private readonly struct OwnedImage(ITerminalCore owner, KittyImage image) : IEquatable<OwnedImage>
    {
        private ITerminalCore Owner { get; } = owner;
        private KittyImage Image { get; } = image;

        public bool Equals(OwnedImage other)
            => ReferenceEquals(Owner, other.Owner) && ReferenceEquals(Image, other.Image);

        public override bool Equals(object? obj) => obj is OwnedImage other && Equals(other);
        public override int GetHashCode()
            => HashCode.Combine(RuntimeHelpers.GetHashCode(Owner), RuntimeHelpers.GetHashCode(Image));
    }
}
