using System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace KeyDisplay
{
    // 1.9：组合模式手柄底板 —— 依据 5 个高星开源 overlay 项目的实测结论设计：
    //   · keyviz(9.7k★) / BongoCat(23.7k★) / input-overlay(4.2k★) / gamepadviewer 都不画任何内部细节，
    //     机身只留极低不透明度的细轮廓（input-overlay xbox 握把实测 alpha≈12%）；
    //   · 对比全部由"底板几乎看不见 vs 按键 100% 平涂"提供，不靠阴影/渐变/发光。
    // 我们的按键是暗底，所以底板必须"压暗"而不是"提亮"：填充 = 黑 @12%（input-overlay 机身实测值），
    // 描边 = 边框色 @35% 1px。
    // 底板顶边 y=26 在肩键/扳机（y=8~22）之下 —— 肩键与扳机 100% 位于底板之外。
    public sealed partial class Widget1
    {
        private const double GpFrameX = 4, GpFrameY = 26, GpFrameW = 168, GpFrameH = 81, GpFrameR = 14;
        private const double GpFrameFillOpacity = 0.12;
        private const double GpFrameStrokeOpacity = 0.35;

        private void GpOutlineBuild(string tpl)
        {
            try
            {
                var cv = GamepadTemplatePanel;
                if (cv == null) return;
                cv.Children.Clear();
                var frame = new Rectangle
                {
                    Width = GpFrameW,
                    Height = GpFrameH,
                    RadiusX = GpFrameR,
                    RadiusY = GpFrameR,
                    Fill = GpDim(new SolidColorBrush(Windows.UI.Colors.Black), GpFrameFillOpacity),
                    Stroke = GpDim(BorderB(), GpFrameStrokeOpacity),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(frame, GpFrameX);
                Canvas.SetTop(frame, GpFrameY);
                cv.Children.Add(frame);
            }
            catch (Exception ex) { DiagLog("gamepad frame build fail: " + ex.Message); }
        }

        // 取同色但降低不透明度的画刷（不改动主题画刷本身，避免影响其它界面）
        private static Brush GpDim(Brush b, double opacity)
        {
            try
            {
                var s = b as SolidColorBrush;
                if (s != null) return new SolidColorBrush(s.Color) { Opacity = opacity };
            }
            catch { }
            return b;
        }
    }
}