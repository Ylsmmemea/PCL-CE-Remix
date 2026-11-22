using System.Collections.Generic;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Core.Server;

namespace PCL;

// 左栏在列表态整块收起 进了服务器之后才是这台服务器的分页菜单
public partial class PageServerLeft
{
    public ServerInstance? CurrentInstance;

    private readonly List<MyListItem> _items = [];
    private bool _suppressCheck;

    public PageServerLeft()
    {
        InitializeComponent();
        _BuildMenu();
    }

    // 列表态 整块收起来 左栏跟着收成 0 宽
    public void ShowList()
    {
        CurrentInstance = null;
        PanMenu.Visibility = Visibility.Collapsed;
    }

    // 进某台服务器 显示它的分页菜单
    public void ShowMenu(ServerInstance instance)
    {
        CurrentInstance = instance;
        LabServer.Text = instance.Name;
        PanMenu.Visibility = Visibility.Visible;
        Select(ServerSection.Home);
    }

    public void Select(ServerSection section)
    {
        _suppressCheck = true;
        try
        {
            foreach (var item in _items) item.Checked = item.Tag is ServerSection target && target == section;
        }
        finally
        {
            _suppressCheck = false;
        }
    }

    private void _BuildMenu()
    {
        var back = new MyListItem
        {
            Title = Lang.Text("Server.Menu.Back"),
            SvgIcon = "lucide/chevron-left",
            Height = 38d,
            VerticalAlignment = VerticalAlignment.Top,
            Type = MyListItem.CheckType.Clickable
        };
        back.Click += (_, _) => ModMain.frmServerRight?.ExitToList();
        PanItems.Children.Add(back);

        _AddItem(ServerSection.Home, "lucide/layout-dashboard", "Server.Menu.Home");
        _AddItem(ServerSection.Console, "lucide/scroll-text", "Server.Menu.Console");
        _AddItem(ServerSection.Network, "lucide/network", "Server.Menu.Network");
        _AddItem(ServerSection.Files, "lucide/folder-open", "Server.Menu.Files");
    }

    private void _AddItem(ServerSection section, string icon, string langKey)
    {
        var item = new MyListItem
        {
            Title = Lang.Text(langKey),
            SvgIcon = icon,
            Height = 38d,
            VerticalAlignment = VerticalAlignment.Top,
            Type = MyListItem.CheckType.RadioBox,
            Tag = section
        };
        item.Check += (_, _) =>
        {
            if (_suppressCheck) return;
            ModMain.frmServerRight?.ShowSection(section);
        };
        _items.Add(item);
        PanItems.Children.Add(item);
    }
}
