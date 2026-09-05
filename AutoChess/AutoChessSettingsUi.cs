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

        bool shouldShow = TryFindVisibleModAnchor(tree.Root, out Control? anchor);
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
    private bool TryFindVisibleModAnchor(Node node, out Control? anchor)
    {
        anchor = null;
        int bestScore = 0;

        try
        {
            Walk(node, ref anchor, ref bestScore);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return anchor != null;
    }

    private void Walk(Node node, ref Control? anchor, ref int bestScore)
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

            if (node is Label or RichTextLabel)
            {
                string text = GetLabelText((Control)node);
                int score = GetAnchorScore(text);
                if (score > bestScore)
                {
                    bestScore = score;
                    anchor = (Control)node;
                }
            }

            foreach (Node child in node.GetChildren())
            {
                Walk(child, ref anchor, ref bestScore);
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
        float width = Math.Min(460f, Math.Max(300f, viewportSize.X - 48f));
        _settingsButton!.CustomMinimumSize = new Vector2(width, 52);
        _settingsButton.Size = new Vector2(width, 52);

        // 优先把按钮放到右侧 mod 描述块的下方，而不是整个窗口底边居中。
        // 这样视觉上会更像“该 Mod 自己的设置入口”，也更符合当前管理页布局。
        if (IsValid(anchor))
        {
            Vector2 anchorPos = anchor!.GlobalPosition;
            float anchorBottom = anchorPos.Y + anchor.Size.Y;
            float x = Math.Max(24f, Math.Min(anchorPos.X, viewportSize.X - width - 24f));
            float y = Math.Max(24f, Math.Min(anchorBottom + 12f, viewportSize.Y - 72f));
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

    private static string GetLabelText(Control label)
    {
        return label switch
        {
            Label normal => normal.Text,
            RichTextLabel rich => rich.Text,
            _ => string.Empty,
        };
    }

    private static int GetAnchorScore(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        // 描述文本优先级最高：它通常同时包含“利息 / 合成 / 刷新”这几个关键词。
        if (text.Contains("利息", StringComparison.OrdinalIgnoreCase)
            && text.Contains("合成", StringComparison.OrdinalIgnoreCase)
            && text.Contains("刷新", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (text.Contains("自走棋式金币利息", StringComparison.OrdinalIgnoreCase)
            || text.Contains("商店刷新和卡牌合成", StringComparison.OrdinalIgnoreCase))
        {
            return 95;
        }

        if (text.Contains("AutoChessTactics", StringComparison.OrdinalIgnoreCase)
            || text.Contains("自走棋战术", StringComparison.OrdinalIgnoreCase))
        {
            return 70;
        }

        if (text.Contains("Author:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Version:", StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        return 0;
    }

    private Button CreateSettingsButton()
    {
        var button = new Button
        {
            // 和 AncientWaifus 的 Mod 页面设置入口保持相似：底部居中的文字按钮。
            Text = "AutoChess Config (Settings)",
            Flat = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = "设置利息、合成、商店刷新和删牌费用",
        };
        button.AddThemeColorOverride("font_color", new Color(0.95f, 0.82f, 0.32f));
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeFontSizeOverride("font_size", 28);
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

        var close = new Button { Text = "关闭" };
        close.Pressed += () => panel.Visible = false;
        rows.AddChild(close);
        return panel;
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
