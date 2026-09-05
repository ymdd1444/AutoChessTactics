using System;
using System.Collections.Generic;
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
    private readonly HashSet<Control> _trackedLabels = new();
    private double _scanTimer;
    private Button? _settingsButton;
    private PanelContainer? _popup;

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
        tree.NodeAdded += _instance.OnNodeAdded;
        tree.NodeRemoved += _instance.OnNodeRemoved;
        _instance.TrackRecursive(tree.Root);
        Log.Info("[AutoChessTactics] 设置界面扫描器已启动。");
    }

    private void OnNodeAdded(Node node)
    {
        if (node is Label or RichTextLabel)
        {
            _trackedLabels.Add((Control)node);
        }
    }

    private void OnNodeRemoved(Node node)
    {
        if (node is Label or RichTextLabel)
        {
            _trackedLabels.Remove((Control)node);
        }
    }

    private void TrackRecursive(Node node)
    {
        if (node is Label or RichTextLabel)
        {
            _trackedLabels.Add((Control)node);
        }

        foreach (Node child in node.GetChildren())
        {
            TrackRecursive(child);
        }
    }

    public override void _Process(double delta)
    {
        _scanTimer += delta;
        if (_scanTimer < 0.5)
        {
            return;
        }

        _scanTimer = 0;
        TryInjectButton();
    }

    private void TryInjectButton()
    {
        _trackedLabels.RemoveWhere(label =>
            !GodotObject.IsInstanceValid(label) || !label.IsInsideTree());

        Control? target = null;
        foreach (Control label in _trackedLabels)
        {
            if (!label.Visible || !IsThisModLabel(GetLabelText(label)))
            {
                continue;
            }

            Node? parent = label.GetParent();
            while (parent != null && parent is not VBoxContainer)
            {
                parent = parent.GetParent();
            }

            target = parent as Control ?? label;
            break;
        }

        if (target == null)
        {
            if (_settingsButton != null && GodotObject.IsInstanceValid(_settingsButton))
            {
                _settingsButton.Visible = false;
            }
            return;
        }

        _settingsButton ??= CreateSettingsButton();
        if (_settingsButton.GetParent() != target)
        {
            _settingsButton.GetParent()?.RemoveChild(_settingsButton);
            target.AddChild(_settingsButton);
        }

        _settingsButton.Visible = true;
        _settingsButton.MoveToFront();
        _settingsButton.CustomMinimumSize = new Vector2(300, 52);
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

    private static bool IsThisModLabel(string text)
    {
        return text.Contains("AutoChessTactics", StringComparison.OrdinalIgnoreCase)
            || text.Contains("自走棋战术", StringComparison.OrdinalIgnoreCase);
    }

    private Button CreateSettingsButton()
    {
        var button = new Button
        {
            Text = "自走棋设置",
            Flat = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = "设置利息、合成、商店刷新和删牌费用",
        };
        button.AddThemeColorOverride("font_color", new Color(0.95f, 0.82f, 0.32f));
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.Pressed += ShowPopup;
        return button;
    }

    private void ShowPopup()
    {
        if (_popup == null || !GodotObject.IsInstanceValid(_popup))
        {
            _popup = CreatePopup();
            if (Engine.GetMainLoop() is not SceneTree tree)
            {
                return;
            }

            var layer = new CanvasLayer { Layer = 100 };
            layer.AddChild(_popup);
            tree.Root.AddChild(layer);
        }

        _popup.Visible = true;
        _popup.MoveToFront();
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
