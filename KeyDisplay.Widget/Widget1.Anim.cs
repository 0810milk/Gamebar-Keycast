using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;

namespace KeyDisplay
{
    // ===================== 整体动画（0.8.2 第一阶段：弹层淡入 + 启动淡入，由轻到重） =====================
    // 思路：所有弹层均为 Visibility 控制，接入统一的 Storyboard 淡入即可获得顺滑感；
    // 按键点亮等高频动画与每帧状态刷新冲突，留待后续阶段再做。
    public sealed partial class Widget1 : Page
    {
        // 弹层淡入：opacity 从 0 → 1（160ms，CubicEase 缓出）；元素为空或动画环境异常时静默跳过
        private void FadeIn(UIElement el, double ms = 160)
        {
            if (el == null) return;
            try
            {
                if (ms <= 0) ms = 160;
                el.Opacity = 0;
                var anim = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(anim, el);
                Storyboard.SetTargetProperty(anim, "Opacity");
                var sb = new Storyboard();
                sb.Children.Add(anim);
                sb.Begin();
            }
            catch { }
        }

        // 启动淡入：整体从透明渐显（OnLoaded 调用一次，动画结束后 opacity=1 无残留）
        private void StartupFadeIn(UIElement el, double ms = 320)
        {
            if (el == null) return;
            try
            {
                el.Opacity = 0;
                FadeIn(el, ms);
            }
            catch { }
        }
    }
}