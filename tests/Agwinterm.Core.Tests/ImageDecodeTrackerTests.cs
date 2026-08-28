using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class ImageDecodeTrackerTests
{
    private static KittyImage Image(int id, byte tag)
        => new(id, KittyFormat.Bgra, 1, 1, [tag, 0, 0, 255]);

    [Fact]
    public void PaneLocalIdsDoNotSupersedeEachOther()
    {
        var tracker = new ImageDecodeTracker();
        var left = new TerminalEmulator(10, 2);
        var right = new TerminalEmulator(10, 2);
        KittyImage leftImage = Image(1, 1);
        KittyImage rightImage = Image(1, 2);

        tracker.Publish(left, [leftImage]);
        tracker.Publish(right, [rightImage]);

        Assert.True(tracker.IsLatest(left, leftImage));
        Assert.True(tracker.IsLatest(right, rightImage));
        Assert.True(tracker.TryStart(left, leftImage, 2));
        Assert.True(tracker.TryStart(right, rightImage, 2));
    }

    [Fact]
    public void PublishingOnePaneDoesNotPruneAnotherPane()
    {
        var tracker = new ImageDecodeTracker();
        var left = new TerminalEmulator(10, 2);
        var right = new TerminalEmulator(10, 2);
        KittyImage leftImage = Image(1, 1);
        KittyImage rightImage = Image(2, 2);

        tracker.Publish(left, [leftImage]);
        tracker.Publish(right, [rightImage]);
        tracker.Publish(left, []);

        Assert.False(tracker.IsLatest(left, leftImage));
        Assert.True(tracker.IsLatest(right, rightImage));
    }

    [Fact]
    public void RapidReplacementKeepsOnlyTheNewestInstanceCurrent()
    {
        var tracker = new ImageDecodeTracker();
        var owner = new TerminalEmulator(10, 2);
        KittyImage oldImage = Image(1, 1);
        KittyImage newImage = Image(1, 2);

        tracker.Publish(owner, [oldImage]);
        Assert.True(tracker.TryStart(owner, oldImage, 2));
        tracker.Publish(owner, [newImage]);

        Assert.False(tracker.IsLatest(owner, oldImage));
        Assert.True(tracker.IsLatest(owner, newImage));
        Assert.True(tracker.TryStart(owner, newImage, 2));
    }

    [Fact]
    public void CompletingOldWorkMakesCapacityAvailableToTheLatestFrame()
    {
        var tracker = new ImageDecodeTracker();
        var left = new TerminalEmulator(10, 2);
        var right = new TerminalEmulator(10, 2);
        var third = new TerminalEmulator(10, 2);
        KittyImage leftImage = Image(1, 1);
        KittyImage rightImage = Image(1, 2);
        KittyImage waitingImage = Image(1, 3);

        tracker.Publish(left, [leftImage]);
        tracker.Publish(right, [rightImage]);
        tracker.Publish(third, [waitingImage]);
        Assert.True(tracker.TryStart(left, leftImage, 2));
        Assert.True(tracker.TryStart(right, rightImage, 2));
        Assert.False(tracker.TryStart(third, waitingImage, 2));

        tracker.Complete(left, leftImage);

        Assert.Equal(1, tracker.InFlightCount);
        Assert.True(tracker.TryStart(third, waitingImage, 2));
    }

    [Fact]
    public void HiddenOwnersCannotPublishCompletedStaleWork()
    {
        var tracker = new ImageDecodeTracker();
        var visible = new TerminalEmulator(10, 2);
        var hidden = new TerminalEmulator(10, 2);
        KittyImage visibleImage = Image(1, 1);
        KittyImage hiddenImage = Image(1, 2);
        tracker.Publish(visible, [visibleImage]);
        tracker.Publish(hidden, [hiddenImage]);

        tracker.RetainOwners([visible]);

        Assert.True(tracker.IsLatest(visible, visibleImage));
        Assert.False(tracker.IsLatest(hidden, hiddenImage));
    }
}
