using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace AutoChessTactics;

/// <summary>
/// 轻量提示文字（toast）：在屏幕顶部中央显示一行文字，1.8 秒后自动消失。
/// 用于展示“利息 +X 金币”等短消息。
/// </summary>
public static class UiToast
{
    /// <summary>在屏幕顶部显示一条提示。</summary>
    public static void Show(string text)
    {
        NRun? run = NRun.Instance;
        if (run?.GlobalUi == null)
        {
            return;
        }

        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1f, 0.85f, 0.3f),
        };
        // 主题覆盖：字号大一点、加粗效果（用默认字体）
        label.AddThemeFontSizeOverride("font_size", 30);

        // 放在全局 UI 最上层
        run.GlobalUi.AddChild(label);

        // 锚点：顶部水平居中
        label.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        label.Position = new Vector2(label.Position.X, 90f);

        // 1.8 秒后自动销毁
        SceneTreeTimer? timer = label.GetTree()?.CreateTimer(1.8);
        if (timer != null)
        {
            timer.Timeout += label.QueueFree;
        }
        else
        {
            label.QueueFree();
        }
    }
}
