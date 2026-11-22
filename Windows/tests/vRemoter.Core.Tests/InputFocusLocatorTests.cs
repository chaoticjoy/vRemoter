using vRemoter.Core.Focus;
using Xunit;

namespace vRemoter.Core.Tests;

public sealed class InputFocusLocatorTests
{
    private sealed class FakeNode
    {
        public string? Role { get; set; }
        public string? Subrole { get; set; }
        public string? Identifier { get; set; }
        public string? DomIdentifier { get; set; }
        public string? Placeholder { get; set; }
        public string? DescriptionText { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Hidden { get; set; }
        public bool Focused { get; set; }
        public bool FocusedSettable { get; set; } = true;
        public bool Modal { get; set; }
        public UiRect? Rect { get; set; } = new(100, 100, 400, 48);
        public FakeNode? Parent { get; private set; }
        public List<FakeNode> Children { get; } = [];

        public FakeNode(
            string? role,
            string? subrole = null,
            string? identifier = null,
            string? domIdentifier = null,
            string? placeholder = null,
            string? descriptionText = null,
            UiRect? rect = null)
        {
            Role = role;
            Subrole = subrole;
            Identifier = identifier;
            DomIdentifier = domIdentifier;
            Placeholder = placeholder;
            DescriptionText = descriptionText;
            Rect = rect ?? new UiRect(100, 100, 400, 48);
        }

        public void AddChild(FakeNode child)
        {
            child.Parent = this;
            Children.Add(child);
        }
    }

    private sealed class FakeUiBackend : IUiTreeBackend<FakeNode>
    {
        public List<FakeNode> FocusRequests { get; } = [];
        public bool AcceptFocus { get; set; } = true;

        public bool AreEqual(FakeNode? left, FakeNode? right) => ReferenceEquals(left, right);
        public string? GetRole(FakeNode node) => node.Role;
        public string? GetSubrole(FakeNode node) => node.Subrole;
        public string? GetIdentifier(FakeNode node) => node.Identifier;
        public string? GetDomIdentifier(FakeNode node) => node.DomIdentifier;
        public string? GetPlaceholder(FakeNode node) => node.Placeholder;
        public string? GetDescriptionText(FakeNode node) => node.DescriptionText;
        public bool IsEnabled(FakeNode node) => node.Enabled;
        public bool IsHidden(FakeNode node) => node.Hidden;
        public bool IsFocused(FakeNode node) => node.Focused;
        public bool IsFocusedSettable(FakeNode node) => node.FocusedSettable;
        public bool IsModal(FakeNode window) => window.Modal;
        public UiRect? GetRect(FakeNode node) => node.Rect;
        public FakeNode? GetParent(FakeNode node) => node.Parent;
        public IReadOnlyList<FakeNode>? GetChildren(FakeNode node) => node.Children;

        public bool SetFocused(FakeNode node)
        {
            FocusRequests.Add(node);
            if (AcceptFocus) node.Focused = true;
            return AcceptFocus;
        }
    }

    private static FakeNode MakeWindow() => new("AXWindow", rect: new UiRect(0, 0, 1200, 800));

    [Fact]
    public void DiscoversChatInputAndIgnoresSearch()
    {
        var backend = new FakeUiBackend();
        var window = MakeWindow();
        var webArea = new FakeNode("AXWebArea", rect: new UiRect(0, 0, 1200, 800));
        var search = new FakeNode("AXTextField", placeholder: "Search files", rect: new UiRect(20, 20, 200, 30));
        var chatInput = new FakeNode("Document", descriptionText: "Message input", rect: new UiRect(700, 700, 450, 48));
        window.AddChild(search);
        window.AddChild(webArea);
        webArea.AddChild(chatInput);

        var discovered = InputFocusDiscovery.DiscoverTarget(backend, window, allowSingleFallback: false);
        Assert.Same(chatInput, discovered);
    }

    [Fact]
    public void DiscoversInputBeyond700Nodes()
    {
        var backend = new FakeUiBackend();
        var window = MakeWindow();
        var group = new FakeNode("AXGroup", rect: new UiRect(0, 0, 1200, 800));
        window.AddChild(group);
        for (int i = 0; i < 950; i++)
        {
            group.AddChild(new FakeNode("Text"));
        }
        var chatInput = new FakeNode("Edit", descriptionText: "输入消息", rect: new UiRect(700, 700, 450, 48));
        group.AddChild(chatInput);

        var discovered = InputFocusDiscovery.DiscoverTarget(backend, window, allowSingleFallback: false);
        Assert.Same(chatInput, discovered);
    }

    [Fact]
    public void ProtectsSearchFieldButPullsFromNonSearchCodeEditor()
    {
        var backend = new FakeUiBackend();
        var window = MakeWindow();
        var search = new FakeNode("AXTextField", placeholder: "Search");
        var codeEditor = new FakeNode("AXTextArea", descriptionText: "build-driver.sh，预览");
        var chatInput = new FakeNode("AXTextArea", descriptionText: "Message input");
        window.AddChild(search);
        window.AddChild(codeEditor);
        window.AddChild(chatInput);

        var searchStatus = InputFocusDiscovery.Restore(
            backend, window, chatInput, search,
            allowOverrideOtherEditor: false,
            allowOverrideNonSearchEditor: true,
            () => true, () => false, () => false);
        Assert.Equal(InputFocusResultStatus.Editing, searchStatus);
        Assert.Empty(backend.FocusRequests);

        var codeStatus = InputFocusDiscovery.Restore(
            backend, window, chatInput, codeEditor,
            allowOverrideOtherEditor: false,
            allowOverrideNonSearchEditor: true,
            () => true, () => false, () => false);
        Assert.Equal(InputFocusResultStatus.Restored, codeStatus);
        Assert.Same(chatInput, backend.FocusRequests[0]);
    }

    [Fact]
    public void SingleFallbackIgnoresTinyStub()
    {
        var backend = new FakeUiBackend();
        var window = MakeWindow();
        var tinyStub = new FakeNode("AXTextArea", rect: new UiRect(0, 0, 2, 2));
        var mainInput = new FakeNode("AXTextArea", rect: new UiRect(100, 500, 500, 60));
        window.AddChild(tinyStub);
        window.AddChild(mainInput);

        Assert.Null(InputFocusDiscovery.DiscoverTarget(backend, window, allowSingleFallback: false));
        Assert.Same(mainInput, InputFocusDiscovery.DiscoverTarget(backend, window, allowSingleFallback: true));
    }

    [Fact]
    public void LocatorDescribeResolveAndVersionMismatch()
    {
        var backend = new FakeUiBackend();
        var window = MakeWindow();
        var panel = new FakeNode("AXGroup", identifier: "agent-panel", rect: new UiRect(600, 0, 600, 800));
        var composer = new FakeNode("AXGroup", identifier: "composer", rect: new UiRect(600, 650, 580, 120));
        var input = new FakeNode("AXTextArea", descriptionText: "Message input", rect: new UiRect(620, 700, 500, 48));
        var sendBtn = new FakeNode("AXButton", identifier: "send-btn", rect: new UiRect(1130, 700, 32, 32));
        window.AddChild(panel);
        panel.AddChild(composer);
        composer.AddChild(input);
        composer.AddChild(sendBtn);

        string? build = InputFocusLocator.BuildDigest("Antigravity.exe", "100", "1.0.0");
        var locator = InputFocusLocatorBuilder.Describe(backend, window, input, build);
        Assert.NotNull(locator);
        Assert.Same(input, InputFocusLocatorBuilder.Resolve(backend, window, locator!, build));

        string? wrongBuild = InputFocusLocator.BuildDigest("Antigravity.exe", "101", "1.0.1");
        Assert.Null(InputFocusLocatorBuilder.Resolve(backend, window, locator!, wrongBuild));
    }

    [Fact]
    public void ModalWindowAndUserInteractionAbortRestore()
    {
        var backend = new FakeUiBackend();
        var window = MakeWindow();
        window.Modal = true;
        var input = new FakeNode("AXTextArea", descriptionText: "Message input");
        window.AddChild(input);

        var blocked = InputFocusDiscovery.Restore(
            backend, window, input, null,
            allowOverrideOtherEditor: true,
            allowOverrideNonSearchEditor: true,
            () => true, () => false, () => false);
        Assert.Equal(InputFocusResultStatus.Blocked, blocked);

        window.Modal = false;
        var changed = InputFocusDiscovery.Restore(
            backend, window, input, null,
            allowOverrideOtherEditor: false,
            allowOverrideNonSearchEditor: false,
            () => true, () => true, () => false);
        Assert.Equal(InputFocusResultStatus.Changed, changed);
        Assert.Empty(backend.FocusRequests);
    }
}

