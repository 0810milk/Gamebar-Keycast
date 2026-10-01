using System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Shapes;

namespace KeyDisplay
{
    // 1.6：组合模式的手柄模板 = 一个圆角矩形框（用户要求：不要手柄形状，直接框起来就好）
    // 框的范围只包住机身内的部件（摇杆 / 十字键 / ABXY / View / Menu / Guide），
    // 肩键（LB/RB）与扳机（LT/RT）留在框外上方 —— 框顶边 (y=33) 正好在扳机条底边 (y=32) 之下。
    // 自定义模式不画任何模板（由 ApplyGamepadTemplate 提前返回）。
    public sealed partial class Widget1
    {
        private const double GpFrameX = 6, GpFrameY = 33, GpFrameW = 164, GpFrameH = 79, GpFrameR = 12;

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
                    Stroke = BorderB(),
                    StrokeThickness = 1.4,
                    Fill = _transparent,
                    Opacity = 0.9,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(frame, GpFrameX);
                Canvas.SetTop(frame, GpFrameY);
                cv.Children.Add(frame);
            }
            catch (Exception ex) { DiagLog("gamepad frame build fail: " + ex.Message); }
        }
    }
}