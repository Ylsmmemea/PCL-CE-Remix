using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Server;
using PCL.Core.UI.Controls.SvgIcon;

namespace PCL;

// 服务器列表的卡片墙 一行三个 5:4 卡片
// 点卡片进这个服务器的管理界面 启停和删除在卡片上直接点
public partial class ServerWall
{
    private const int Columns = 3;
    private const double Gap = 15;

    // 5:4 的卡片 高 = 宽 * 0.8
    private const double Ratio = 0.8;

    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0xA8, 0x4A));

    private double _cardWidth;

    public ServerWall()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        PanCards.SizeChanged += (_, _) => _LayoutCards();
        // 进程启停后卡片上的状态和按钮要跟着换
        ServerRuntime.StateChanged += Refresh;
    }

    // 重建整个卡片墙 增删实例和状态变化后调
    // 状态事件可能来自后台线程 这里自己回到 UI 线程
    public void Refresh()
    {
        if (!ModBase.RunInUi())
        {
            ModBase.RunInUi(Refresh);
            return;
        }

        PanCards.Children.Clear();
        var instances = ServerInstance.Enumerate();
        LabCount.Text = Lang.Text("Server.Wall.Count", instances.Count);
        LabEmpty.Visibility = instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var instance in instances) PanCards.Children.Add(_CreateCard(instance));
        _LayoutCards();
    }

    // 按当前可用宽度算卡片尺寸 宽度没变就不动 免得 SizeChanged 反复触发
    private void _LayoutCards()
    {
        var available = PanCards.ActualWidth;
        if (available <= Gap * Columns) return;

        var width = Math.Floor((available - Gap * Columns) / Columns);
        if (Math.Abs(width - _cardWidth) < 0.5d) return;
        _cardWidth = width;

        foreach (var child in PanCards.Children)
        {
            if (child is not MyCard card) continue;
            card.Width = width;
            card.Height = Math.Round(width * Ratio);
        }
    }

    private MyCard _CreateCard(ServerInstance instance)
    {
        var width = _cardWidth > 0 ? _cardWidth : 240d;
        var running = ServerRuntime.IsRunning(instance);

        var card = new MyCard
        {
            Width = width,
            Height = Math.Round(width * Ratio),
            Margin = new Thickness(0, 0, Gap, Gap),
            HasMouseAnimation = true,
            Tag = instance
        };

        var root = new Grid { Margin = new Thickness(18, 15, 18, 13) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 第一行 图标 名字 运行状态
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new SvgIcon
        {
            Icon = "lucide/server",
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(SvgIcon.IconBrushProperty, "ColorBrush3");
        header.Children.Add(icon);

        var name = new TextBlock
        {
            Text = instance.Name,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(9, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);

        var status = new TextBlock
        {
            Text = Lang.Text(running ? "Server.Wall.Running" : "Server.Wall.Stopped"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (running) status.Foreground = RunningBrush;
        else status.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
        Grid.SetColumn(status, 2);
        header.Children.Add(status);
        root.Children.Add(header);

        // 中间 版本和端口
        var detail = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        detail.Children.Add(new TextBlock
        {
            Text = instance.IsInstalled ? instance.Version : Lang.Text("Server.Status.NotInstalled"),
            FontSize = 20,
            Opacity = 0.85,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        detail.Children.Add(new TextBlock
        {
            Text = Lang.Text("Server.Wall.Detail", instance.Port, instance.MemoryMb),
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(0, 5, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetRow(detail, 1);
        root.Children.Add(detail);

        // 底部 悬停才出来的操作按钮
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0d
        };

        if (running)
        {
            actions.Children.Add(_CreateButton("lucide/refresh-cw", Lang.Text("Server.Action.Restart"), 0,
                () => ServerRuntime.Restart(instance)));
            actions.Children.Add(_CreateButton("lucide/power", Lang.Text("Server.Action.Stop"), 10,
                () => ServerRuntime.Stop(instance)));
        }
        else
        {
            actions.Children.Add(_CreateButton("lucide/play", Lang.Text("Server.Action.Start"), 0,
                () => ServerRuntime.Start(instance)));
        }

        actions.Children.Add(_CreateButton("lucide/trash-2", Lang.Text("Server.Action.Delete"), 10,
            () => DeleteInstance(instance)));

        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        card.Children.Add(root);
        card.MouseEnter += (_, _) => actions.Opacity = 1d;
        card.MouseLeave += (_, _) => actions.Opacity = 0d;
        card.MouseLeftButtonUp += (_, _) => ModMain.frmServerRight?.Enter(instance);
        return card;
    }

    private static MyIconButton _CreateButton(string icon, string tooltip, double margin, Action action)
    {
        var button = new MyIconButton
        {
            SvgIcon = icon,
            ToolTip = tooltip,
            LogoScale = 0.85,
            Width = 26,
            Height = 26,
            Margin = new Thickness(margin, 0, 0, 0)
        };
        button.Click += (_, _) => action();
        return button;
    }

    #region 新建与删除

    private void BtnNew_Click(object sender, MouseButtonEventArgs e) => ServerInstallLib.CreateServerFlow();

    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e) => Refresh();

    // 运行中的实例不能删 世界里还有人在玩
    public void DeleteInstance(ServerInstance instance)
    {
        if (ServerRuntime.IsRunning(instance))
        {
            HintService.Hint(Lang.Text("Server.Delete.Running"), HintType.Error);
            return;
        }

        ModMain.MyMsgBox(
            Lang.Text("Server.Delete.Confirm", instance.Name),
            Lang.Text("Server.Delete.Title"),
            button2: Lang.Text("Common.Action.Cancel"),
            isWarn: true,
            button1Action: () =>
            {
                try
                {
                    instance.Delete();
                    if (Config.Server.SelectedName == instance.Name) Config.Server.SelectedName = "";
                    Refresh();
                    HintService.Hint(Lang.Text("Server.Delete.Success", instance.Name), HintType.Success);
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "[Server] 删除实例失败");
                    HintService.Hint(Lang.Text("Server.Delete.Failed"), HintType.Error);
                }
            });
    }

    #endregion
}
