using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Porchlight.App;

// Segmented derives from ListViewBase, but unlike ListView it doesn't create a selection peer.
public sealed class AccessibleSegmented : Segmented
{
    protected override AutomationPeer OnCreateAutomationPeer() => new SegmentedPeer(this);

    sealed class SegmentedPeer(Segmented owner) : ListViewBaseAutomationPeer(owner)
    {
        protected override ItemAutomationPeer OnCreateItemAutomationPeer(object item) => new ListViewItemDataAutomationPeer(item, this);
    }
}
