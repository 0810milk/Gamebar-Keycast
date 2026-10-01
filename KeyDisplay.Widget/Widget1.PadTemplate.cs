using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace KeyDisplay
{
    // 2.0：组合模式手柄底板 —— 按用户提供的线稿参考实现：
    //   · 机身 = 一条连续闭合轮廓（顶部大圆角 + 两侧向下外扩成握把 + 底部中央平坦浅缺口）；
    //   · 机身内四个「井」（浅色圆环）：左摇杆 / ABXY / 十字键 / 右摇杆，位置与部件坐标一致；
    //   · 井内再画深色内圈（摇杆）或深色四瓣（ABXY、十字键），形成"外浅内深"的层次；
    //   · 机身填充极淡（黑 10%），描边细（1.1）；肩键与扳机位于机身顶边之上（y<27），在机身之外。
    // 内部不画任何多余细节（无贴图、无标签、无阴影、无渐变），与主流开源 overlay 做法一致。
    public sealed partial class Widget1
    {
        private const double GpBodyL = 2, GpBodyR = 174, GpBodyTop = 27, GpBodyCorner = 26;
        private const double GpBodyWaist = 74, GpGripR = 34, GpNotchY = 98;
        private const double GpOutline = 1.1;
        private const double GpBodyFill = 0.10;
        private const double GpWellStroke = 0.55;
        private const double GpInnerStroke = 0.45;

        private void GpOutlineBuild(string tpl)
        {
            try
            {
                var cv = GamepadTemplatePanel;
                if (cv == null) return;
                cv.Children.Clear();

                var pts = new List<Point>();
                double xL = GpBodyL, xR = GpBodyR, rc = GpBodyCorner;
                double lgx = xL + GpGripR, rgx = xR - GpGripR;
                for (int i = 0; i <= 14; i++)                     // 左上圆角
                {
                    double a = Math.PI * (1 + i / 14.0 * 0.5);
                    pts.Add(new Point(xL + rc + rc * Math.Cos(a), GpBodyTop + rc + rc * Math.Sin(a)));
                }
                pts.Add(new Point(xR - rc, GpBodyTop));            // 顶边
                for (int i = 0; i <= 14; i++)                     // 右上圆角
                {
                    double a = -Math.PI / 2 + i / 14.0 * (Math.PI / 2);
                    pts.Add(new Point(xR - rc + rc * Math.Cos(a), GpBodyTop + rc + rc * Math.Sin(a)));
                }
                pts.Add(new Point(xR, GpBodyWaist));               // 右侧下行
                for (int i = 1; i <= 22; i++)                     // 右握把外弧（到底部）
                {
                    double a = i / 22.0 * (Math.PI * 0.72);
                    pts.Add(new Point(rgx + GpGripR * Math.Cos(a), GpBodyWaist + GpGripR * Math.Sin(a)));
                }
                double ax = rgx + GpGripR * Math.Cos(Math.PI * 0.72);
                double ay = GpBodyWaist + GpGripR * Math.Sin(Math.PI * 0.72);
                double bx = lgx - GpGripR * Math.Cos(Math.PI * 0.72);
                for (int i = 1; i <= 10; i++)                     // 底部中央：一条水平直线（平坦缺口，与参考图一致）
                {
                    double t = i / 11.0;
                    pts.Add(new Point(ax + (bx - ax) * t, ay));
                }
                for (int i = 22; i >= 0; i--)                     // 左握把外弧
                {
                    double a = i / 22.0 * (Math.PI * 0.72);
                    pts.Add(new Point(lgx - GpGripR * Math.Cos(a), GpBodyWaist + GpGripR * Math.Sin(a)));
                }

                var body = new Polygon
                {
                    Fill = GpDim(new SolidColorBrush(Colors.Black), GpBodyFill),
                    Stroke = GpDim(BorderB(), GpWellStroke),
                    StrokeThickness = GpOutline,
                    IsHitTestVisible = false
                };
                foreach (var p in pts) body.Points.Add(p);
                cv.Children.Add(body);

                // 四个井 + 井内深色图形
                GpWell(cv, 34, 44, 16, true);     // 左摇杆
                GpWell(cv, 126, 48, 18, false);   // ABXY
                GpWell(cv, 34, 84, 17, false);    // 十字键
                GpWell(cv, 126, 88, 16, true);    // 右摇杆
            }
            catch (Exception ex) { DiagLog("gamepad template fail: " + ex.Message); }
        }

        private void GpWell(Canvas cv, double cx, double cy, double r, bool ring)
        {
            try
            {
                var well = new Ellipse
                {
                    Width = r * 2, Height = r * 2,
                    Stroke = GpDim(BorderB(), GpWellStroke),
                    StrokeThickness = GpOutline,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(well, cx - r);
                Canvas.SetTop(well, cy - r);
                cv.Children.Add(well);

                var dark = GpDim(new SolidColorBrush(Colors.Black), GpInnerStroke);
                if (ring)
                {
                    double ri = r * 0.62;
                    var inner = new Ellipse
                    {
                        Width = ri * 2, Height = ri * 2,
                        Stroke = dark, StrokeThickness = GpOutline,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(inner, cx - ri);
                    Canvas.SetTop(inner, cy - ri);
                    cv.Children.Add(inner);
                }
                else
                {
                    double d = r * 0.55, pr = r * 0.32;
                    for (int k = 0; k < 4; k++)
                    {
                        double ang = -Math.PI / 2 + k * Math.PI / 2;
                        double px = cx + Math.Cos(ang) * d, py = cy + Math.Sin(ang) * d;
                        var petal = new Ellipse
                        {
                            Width = pr * 2, Height = pr * 2,
                            Stroke = dark, StrokeThickness = GpOutline,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(petal, px - pr);
                        Canvas.SetTop(petal, py - pr);
                        cv.Children.Add(petal);
                    }
                }
            }
            catch (Exception ex) { DiagLog("gamepad well fail: " + ex.Message); }
        }

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