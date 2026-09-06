using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace AutoChessTactics;

/// <summary>
/// 在游戏 Mod 管理界面里注入“自走棋设置”按钮。
///
/// 游戏没有给所有 Mod 提供统一设置 API，因此采用和 AncientWaifus
/// 相同的轻量方案：扫描场景树中的文本控件，找到本 Mod 条目后注入按钮。
/// </summary>
public sealed partial class AutoChessSettingsUi : Node
{
    private static AutoChessSettingsUi? _instance;
    private double _scanTimer;
    private CanvasLayer? _buttonLayer;
    private Button? _settingsButton;
    private CanvasLayer? _popupLayer;
    private PanelContainer? _popup;
    private Control? _buttonAnchor;

    public static void Initialize()
    {
        if (_instance != null && GodotObject.IsInstanceValid(_instance))
        {
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            return;
        }

        _instance = new AutoChessSettingsUi();
        tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
        Log.Info("[AutoChessTactics] 设置界面扫描器已启动。");
    }

    public override void _Process(double delta)
    {
        _scanTimer += delta;
        if (_scanTimer < 0.5)
        {
            return;
        }

        _scanTimer = 0;
        try
        {
            TryInjectButton();
        }
        catch (ObjectDisposedException)
        {
            // 退出游戏/切换菜单时，Godot 会先销毁一批 UI 节点。
            // 旧版本把这些已销毁节点缓存起来继续访问，会在保存退出时刷屏异常。
            // 这里直接吞掉并隐藏按钮，下一帧如果 Mod 页面还存在会重新扫描出来。
            HideSettingsButton();
        }
        catch
        {
            // 这个扫描器属于菜单辅助 UI，绝不能把异常抛回 Godot 主循环。
            // 保存退出或战斗结算期间日志系统本身也可能在收尾，所以这里不再写日志。
            HideSettingsButton();
        }
    }

    public override void _ExitTree()
    {
        HideSettingsButton();
        if (IsValid(_buttonLayer))
        {
            _buttonLayer!.QueueFree();
        }
        if (IsValid(_popupLayer))
        {
            _popupLayer!.QueueFree();
        }

        _buttonLayer = null;
        _settingsButton = null;
        _popupLayer = null;
        _popup = null;
    }

    private void TryInjectButton()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            HideSettingsButton();
            return;
        }

        bool shouldShow = TryFindSelectedModAnchor(tree.Root, out Control? anchor);
        if (!shouldShow)
        {
            HideSettingsButton();
            return;
        }

        _buttonAnchor = anchor;
        EnsureSettingsButton(tree);
        if (!IsValid(_settingsButton))
        {
            return;
        }

        PositionSettingsButton(_buttonAnchor);
        _settingsButton!.Visible = true;
        _settingsButton.MoveToFront();
    }

    /// <summary>
    /// 每次扫描当前场景树，不缓存 Label 引用。
    /// 菜单切页和保存退出时节点销毁很快，缓存旧 Control 是 ObjectDisposedException 的来源。
    /// </summary>
    private bool TryFindSelectedModAnchor(Node node, out Control? anchor)
    {
        anchor = null;
        int bestScore = 0;
        float bestArea = 0f;

        try
        {
            Walk(node, ref anchor, ref bestScore, ref bestArea);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return anchor != null;
    }

    private void Walk(Node node, ref Control? anchor, ref int bestScore, ref float bestArea)
    {
        if (!IsValid(node))
        {
            return;
        }

        // 跳过本 Mod 自己创建的浮层和弹窗，避免按钮文字让自己永远保持显示。
        if (ReferenceEquals(node, this)
            || (_buttonLayer != null && ReferenceEquals(node, _buttonLayer))
            || (_popupLayer != null && ReferenceEquals(node, _popupLayer))
            || (_popup != null && ReferenceEquals(node, _popup)))
        {
            return;
        }

        try
        {
            if (node is Control control && !IsEffectivelyVisible(control))
            {
                return;
            }

            if (node is Control controlNode)
            {
                int score = GetAnchorScore(controlNode);
                float area = controlNode.Size.X * controlNode.Size.Y;
                if (score > bestScore || (score == bestScore && area > bestArea))
                {
                    bestScore = score;
                    bestArea = area;
                    anchor = controlNode;
                }
            }

            foreach (Node child in node.GetChildren())
            {
                Walk(child, ref anchor, ref bestScore, ref bestArea);
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }

    private static bool IsEffectivelyVisible(Control control)
    {
        for (Node? current = control; current != null; current = current.GetParent())
        {
            if (current is Control parentControl && !parentControl.Visible)
            {
                return false;
            }
        }

        return control.IsInsideTree();
    }

    private void EnsureSettingsButton(SceneTree tree)
    {
        if (!IsValid(_buttonLayer))
        {
            _buttonLayer = new CanvasLayer { Layer = 100 };
            tree.Root.AddChild(_buttonLayer);
        }

        CanvasLayer layer = _buttonLayer!;
        if (!IsValid(_settingsButton))
        {
            _settingsButton = CreateSettingsButton();
            layer.AddChild(_settingsButton);
        }
        else
        {
            // Godot 节点销毁时，托管对象可能还在但底层句柄已经失效。
            // 所以读取 Parent 前也放在 try 里；一旦失败就丢弃旧按钮，下轮重建。
            try
            {
                Button button = _settingsButton!;
                if (button.GetParent() != layer)
                {
                    button.GetParent()?.RemoveChild(button);
                    layer.AddChild(button);
                }
            }
            catch (ObjectDisposedException)
            {
                _settingsButton = null;
            }
        }
    }

    private void PositionSettingsButton(Control? anchor)
    {
        if (!IsValid(_settingsButton))
        {
            return;
        }

        Vector2 viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        float width = Math.Min(360f, Math.Max(260f, viewportSize.X * 0.23f));
        _settingsButton!.CustomMinimumSize = new Vector2(width, 44);
        _settingsButton.Size = new Vector2(width, 44);

        // 只跟随右侧详情面板，不跟左侧列表走。这样按钮会固定在选中本 Mod 的说明下面。
        if (IsValid(anchor))
        {
            Vector2 anchorPos = anchor!.GlobalPosition;
            float x = anchorPos.X + (anchor.Size.X - width) / 2f;
            float anchorBottom = anchorPos.Y + anchor.Size.Y;
            float y = anchorBottom + 10f;
            x = Math.Max(24f, Math.Min(x, viewportSize.X - width - 24f));
            y = Math.Max(24f, Math.Min(y, viewportSize.Y - 60f));
            _settingsButton.Position = new Vector2(x, y);
            return;
        }

        _settingsButton.Position = new Vector2(
            Math.Max(24f, (viewportSize.X - width) / 2f),
            Math.Max(24f, viewportSize.Y - 108f));
    }

    private void HideSettingsButton()
    {
        if (IsValid(_settingsButton))
        {
            _settingsButton!.Visible = false;
        }
    }

    /// <summary>
    /// Godot C# 对象在底层节点释放后仍可能留下托管壳。
    /// 统一用这个方法检查，避免保存退出/界面切换时 ObjectDisposedException 刷屏。
    /// </summary>
    private static bool IsValid(GodotObject? obj)
    {
        try
        {
            return obj != null && GodotObject.IsInstanceValid(obj);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static void ScanSubtreeText(Node node, Action<string> onText)
    {
        if (!IsValid(node))
        {
            return;
        }

        try
        {
            if (node is Label normal)
            {
                onText(normal.Text);
            }
            else if (node is RichTextLabel rich)
            {
                onText(rich.Text);
            }

            foreach (Node child in node.GetChildren())
            {
                ScanSubtreeText(child, onText);
            }
        }
        catch (ObjectDisposedException)
        {
            // 菜单切页时允许节点在扫描期间失效；直接跳过该分支即可。
        }
    }

    private static int GetAnchorScore(Control control)
    {
        if (!IsValid(control))
        {
            return 0;
        }

        // 右侧详情面板通常尺寸更大，左侧列表条目会先被这个门槛过滤掉。
        if (control.Size.X < 260f || control.Size.Y < 180f)
        {
            return 0;
        }

        bool hasTitle = false;
        bool hasAuthor = false;
        bool hasVersion = false;
        bool hasDescription = false;
        ScanSubtreeText(control, text =>
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (text.Contains("AutoChessTactics", StringComparison.OrdinalIgnoreCase)
                || text.Contains("自走棋战术", StringComparison.OrdinalIgnoreCase))
            {
                hasTitle = true;
            }

            if (text.Contains("Author:", StringComparison.OrdinalIgnoreCase))
            {
                hasAuthor = true;
            }

            if (text.Contains("Version:", StringComparison.OrdinalIgnoreCase))
            {
                hasVersion = true;
            }

            if (text.Contains("自走棋式金币利息", StringComparison.OrdinalIgnoreCase)
                || text.Contains("商店刷新和卡牌合成", StringComparison.OrdinalIgnoreCase)
                || text.Contains("金币利息", StringComparison.OrdinalIgnoreCase))
            {
                hasDescription = true;
            }
        });

        if (!hasTitle || !hasAuthor || !hasVersion)
        {
            return 0;
        }

        return 1000 + (hasDescription ? 50 : 0);
    }

    private Button CreateSettingsButton()
    {
        var button = new Button
        {
            // 固定在右侧详情面板下方，不再跟着列表乱跑。
            Text = "AutoChess Config (Settings)",
            Flat = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = "设置利息、合成、商店刷新、删牌和稀有度概率",
        };
        button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        button.AddThemeColorOverride("font_color", new Color(0.95f, 0.82f, 0.32f));
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeFontSizeOverride("font_size", 24);
        button.Pressed += ShowPopup;
        return button;
    }

    private void ShowPopup()
    {
        if (!IsValid(_popup))
        {
            _popup = CreatePopup();
            if (Engine.GetMainLoop() is not SceneTree tree)
            {
                return;
            }

            _popupLayer = new CanvasLayer { Layer = 100 };
            _popupLayer.AddChild(_popup);
            tree.Root.AddChild(_popupLayer);
        }

        PanelContainer popup = _popup!;
        popup.Visible = true;
        popup.MoveToFront();
    }

    private PanelContainer CreatePopup()
    {
        var panel = new PanelContainer
        {
            Name = "AutoChessTacticsSettingsPopup",
            Position = new Vector2(780, 180),
            CustomMinimumSize = new Vector2(520, 0),
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.10f, 0.13f, 0.98f),
            BorderColor = new Color(0.78f, 0.62f, 0.20f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        panel.AddThemeStyleboxOverride("panel", style);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        panel.AddChild(margin);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 12);
        margin.AddChild(rows);

        var title = new Label
        {
            Text = "自走棋设置",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeColorOverride("font_color", new Color(1f, 0.82f, 0.28f));
        title.AddThemeFontSizeOverride("font_size", 26);
        rows.AddChild(title);

        rows.AddChild(CreateNumberRow("利息率 (%)", AutoChessConfig.InterestPercent, 0, 100,
            value =>
            {
                AutoChessConfig.InterestPercent = value;
                AutoChessConfig.Save();
            }));
        rows.AddChild(CreateNumberRow("合成费用", AutoChessConfig.SynthesisCost, 0, 9999,
            value =>
            {
                AutoChessConfig.SynthesisCost = value;
                AutoChessConfig.Save();
            }));
        rows.AddChild(CreateNumberRow("刷新费用", AutoChessConfig.ShopRefreshCost, 0, 9999,
            value =>
            {
                AutoChessConfig.ShopRefreshCost = value;
                AutoChessConfig.Save();
            }));

        var freeRemoval = new CheckButton
        {
            Text = "商店删牌免费",
            ButtonPressed = AutoChessConfig.FreeShopCardRemoval,
        };
        freeRemoval.Toggled += enabled =>
        {
            AutoChessConfig.FreeShopCardRemoval = enabled;
            AutoChessConfig.Save();
        };
        rows.AddChild(freeRemoval);

        rows.AddChild(CreateSectionTitle("稀有度概率"));

        var customRarity = new CheckButton
        {
            Text = "自定义稀有度概率",
            ButtonPressed = AutoChessConfig.CustomCardRarityEnabled,
        };
        customRarity.Toggled += enabled =>
        {
            AutoChessConfig.CustomCardRarityEnabled = enabled;
            AutoChessConfig.Save();
        };
        rows.AddChild(customRarity);

        rows.AddChild(CreateNumberRow("白卡权重 (%)", AutoChessConfig.CustomCardRarityCommonPercent, 0, 100,
            value =>
            {
                AutoChessConfig.CustomCardRarityCommonPercent = value;
                AutoChessConfig.Save();
            }));
        rows.AddChild(CreateNumberRow("蓝卡权重 (%)", AutoChessConfig.CustomCardRarityUncommonPercent, 0, 100,
            value =>
            {
                AutoChessConfig.CustomCardRarityUncommonPercent = value;
                AutoChessConfig.Save();
            }));
        rows.AddChild(CreateNumberRow("金卡权重 (%)", AutoChessConfig.CustomCardRarityRarePercent, 0, 100,
            value =>
            {
                AutoChessConfig.CustomCardRarityRarePercent = value;
                AutoChessConfig.Save();
            }));

        var close = new Button { Text = "关闭" };
        close.Pressed += () => panel.Visible = false;
        rows.AddChild(close);
        return panel;
    }

    private static Label CreateSectionTitle(string text)
    {
        var label = new Label
        {
            Text = text,
        };
        label.AddThemeColorOverride("font_color", new Color(0.95f, 0.82f, 0.32f));
        label.AddThemeFontSizeOverride("font_size", 20);
        return label;
    }

    private static Control CreateNumberRow(
        string title,
        int initialValue,
        int min,
        int max,
        Action<int> onChanged)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);

        var label = new Label
        {
            Text = title,
            CustomMinimumSize = new Vector2(180, 40),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.AddChild(label);

        var spin = new SpinBox
        {
            Value = initialValue,
            MinValue = min,
            MaxValue = max,
            Step = 1,
            AllowLesser = false,
            AllowGreater = false,
            CustomMinimumSize = new Vector2(180, 40),
        };
        spin.ValueChanged += value => onChanged((int)Math.Round(value));
        row.AddChild(spin);
        return row;
    }
}
