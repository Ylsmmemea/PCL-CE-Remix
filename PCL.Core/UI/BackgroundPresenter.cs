using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

// 该部分源码来自或修改于 https://github.com/OrgEleCho/EleCho.WpfSuite
// 项目: EleCho.WpfSuite
// 作者: EleCho
// 协议: MIT License

namespace PCL.Core.UI
{
    /// <summary>
    /// Used for rendering the background content of a UIElement. By adding a <see cref="BlurEffect"/> to this element, you can achieve a blurred background effect.
    /// </summary>
    public class BackgroundPresenter : FrameworkElement
    {
        private static readonly FieldInfo _DrawingContentOfUIElement = typeof(UIElement)
            .GetField("_drawingContent", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static readonly FieldInfo _ContentOfDrawingVisual = typeof(DrawingVisual)
            .GetField("_content", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static readonly FieldInfo _OffsetOfVisual = typeof(Visual)
            .GetField("_offset", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static readonly Func<UIElement, DrawingContext> _RenderOpenMethod = (Func<UIElement, DrawingContext>)typeof(UIElement)
            .GetMethod("RenderOpen", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<UIElement, DrawingContext>));

        private static readonly Action<UIElement, DrawingContext> _OnRenderMethod = (Action<UIElement, DrawingContext>)typeof(UIElement)
            .GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action<UIElement, DrawingContext>));

        private static readonly GetContentBoundsDelegate _MethodGetContentBounds = (GetContentBoundsDelegate)typeof(VisualBrush)
            .GetMethod("GetContentBounds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(GetContentBoundsDelegate));

        private delegate void GetContentBoundsDelegate(VisualBrush visualBrush, out Rect bounds);
        private readonly Stack<UIElement> _parentStack = new();

        public static void ForceRender(UIElement target)
        {
            using var drawingContext = _RenderOpenMethod(target);

            _OnRenderMethod.Invoke(target, drawingContext);
        }

        // 返回是否真的画出了东西
        // 目标视觉要是还没渲染过一次 VisualBrush 的内容边界就是空的 那就什么都不画 调用方得重试
        internal static bool DrawVisual(DrawingContext drawingContext, Visual visual, Point relatedXY)
        {
            var visualBrush = new VisualBrush(visual);
            var visualOffset = (Vector)_OffsetOfVisual.GetValue(visual)!;

            _MethodGetContentBounds.Invoke(visualBrush, out var contentBounds);
            relatedXY -= visualOffset;

            if (contentBounds.IsEmpty)
            {
                return false;
            }

            // 矩形对齐到整像素
            //
            // 背景是把每个祖先和兄弟各画成一个独立矩形拼起来的 这些矩形经常落在分数坐标上
            // 1* 分栏 居中的内容 带小数的边距 都会产生 .5
            // 落在分数边界上的矩形 WPF 会对边缘做抗锯齿 相邻两个各贡献半透明的半像素
            // 合成之后交界处就留下一条 1 像素的缝
            //
            // 普通毛玻璃 16px 模糊能把它抹平 液态玻璃只有 2~3px 缝就原样留在玻璃里 表现为挡板上的竖条
            // 整数对齐之后相邻矩形严丝合缝 缝就没了
            var x0 = Math.Round(relatedXY.X + contentBounds.X);
            var y0 = Math.Round(relatedXY.Y + contentBounds.Y);
            var x1 = Math.Round(relatedXY.X + contentBounds.X + contentBounds.Width);
            var y1 = Math.Round(relatedXY.Y + contentBounds.Y + contentBounds.Height);
            if (x1 <= x0 || y1 <= y0)
            {
                return false;
            }

            drawingContext.DrawRectangle(visualBrush, null, new Rect(x0, y0, x1 - x0, y1 - y0));
            return true;
        }


        /// <inheritdoc/>
        protected override Geometry GetLayoutClip(Size layoutSlotSize)
        {
            return new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        }

        /// <inheritdoc/>
        protected override void OnVisualParentChanged(DependencyObject oldParentObject)
        {
            if (oldParentObject is UIElement oldParent)
            {
                oldParent.LayoutUpdated -= ParentLayoutUpdated;
            }

            if (Parent is UIElement newParent)
            {
                newParent.LayoutUpdated += ParentLayoutUpdated;
            }
        }

        private void ParentLayoutUpdated(object? sender, EventArgs e)
        {
            // cannot use 'InvalidateVisual' here, because it will cause infinite loop

            ForceRender(this);

            // Debug.WriteLine("Parent layout updated, forcing render of BackgroundPresenter.");
        }

        // 返回这次采集有没有成
        // false 表示祖先不可见或者正在布局 什么都没画 调用方得找机会重试
        // 不重试的话这层玻璃就一直空着 直到元素几何变了才会重新采
        internal static bool DrawBackground(
            DrawingContext drawingContext, UIElement self,
            Stack<UIElement> parentStackStorage,
            int maxDepth,
            bool throwExceptionIfParentArranging)
        {
            var selfInDesignMode = DesignerProperties.GetIsInDesignMode(self);

            var parent = VisualTreeHelper.GetParent(self) as UIElement;
            while (
                parent is not null &&
                parentStackStorage.Count < maxDepth)
            {
                // parent not visible, no need to render
                if (!parent.IsVisible)
                {
                    parentStackStorage.Clear();
                    return false;
                }

                if (selfInDesignMode &&
                    parent.GetType().ToString().Contains("VisualStudio"))
                {
                    // 遍历到 VS 自身的设计器元素, 中断!
                    break;
                }

                // is parent arranging
                // we cannot render it
                if (parent.RenderSize.Width == 0 ||
                    parent.RenderSize.Height == 0)
                {
                    parentStackStorage.Clear();

                    if (throwExceptionIfParentArranging)
                    {
                        throw new InvalidOperationException("Arranging");
                    }

                    // render after parent arranging finished
                    self.InvalidateArrange();
                    return false;
                }

                parentStackStorage.Push(parent);
                parent = VisualTreeHelper.GetParent(parent) as UIElement;
            }

            var selfRect = new Rect(0, 0, self.RenderSize.Width, self.RenderSize.Height);
            var drewAnything = false;
            while (parentStackStorage.Count > 0)
            {
                var currentParent = parentStackStorage.Pop();
                var breakElement = self;

                if (parentStackStorage.Count > 0)
                {
                    breakElement = parentStackStorage.Peek();
                }

                var parentRelatedXY = currentParent.TranslatePoint(default, self);

                // has render data
                if (_DrawingContentOfUIElement.GetValue(currentParent) is { } parentDrawingContent)
                {
                    var drawingVisual = new DrawingVisual();
                    _ContentOfDrawingVisual.SetValue(drawingVisual, parentDrawingContent);

                    drewAnything |= DrawVisual(drawingContext, drawingVisual, parentRelatedXY);
                }

                var childCount = VisualTreeHelper.GetChildrenCount(currentParent);
                for (var i = 0; i < childCount; i++)
                {
                    if (VisualTreeHelper.GetChild(currentParent, i) is not UIElement child)
                    {
                        continue;
                    }

                    if (child == breakElement)
                    {
                        break;
                    }

                    var childRelatedXY = child.TranslatePoint(default, self);
                    var childRect = new Rect(childRelatedXY, child.RenderSize);

                    if (!selfRect.IntersectsWith(childRect))
                    {
                        continue; // skip if not intersecting
                    }

                    if (child.IsVisible)
                    {
                        drewAnything |= DrawVisual(drawingContext, child, childRelatedXY);
                    }
                }
            }

            return drewAnything;
        }

        /// <summary>
        /// Draw background of the specified UIElement.
        /// </summary>
        /// <param name="drawingContext"></param>
        /// <param name="self"></param>
        public static void DrawBackground(DrawingContext drawingContext, UIElement self)
        {
            var parentStack = new Stack<UIElement>();
            DrawBackground(drawingContext, self, parentStack, int.MaxValue, true);
        }

        /// <inheritdoc/>
        protected override void OnRender(DrawingContext drawingContext)
        {
            DrawBackground(drawingContext, this, _parentStack, MaxDepth, false);
        }

        /// <summary>
        /// Gets or sets the maximum depth of the visual tree to render.
        /// </summary>
        public int MaxDepth
        {
            get { return (int)GetValue(MaxDepthProperty); }
            set { SetValue(MaxDepthProperty, value); }
        }

        /// <summary>
        /// Dependency property for <see cref="MaxDepth"/>.
        /// </summary>
        public static readonly DependencyProperty MaxDepthProperty =
            DependencyProperty.Register("MaxDepth", typeof(int), typeof(BackgroundPresenter), new FrameworkPropertyMetadata(16));
    }
}
