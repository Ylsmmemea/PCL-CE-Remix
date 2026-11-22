using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PCL.Core.App.Localization;
using PCL.Core.Server;

namespace PCL;

// 服务器实例的文件管理 浏览实例目录 改文本 传文件
// 路径一律以实例目录为根 只允许往下走 不往外跑
public partial class ServerFilePanel
{
    // 超过这个大小就不丢给编辑器了 打开也是卡
    private const long MaxEditableBytes = 1024 * 1024;

    // 能当文本编辑的扩展名 其余一律按二进制处理 免得存回去把文件写坏
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".json", ".yml", ".yaml", ".properties", ".toml", ".ini", ".conf", ".cfg",
        ".log", ".md", ".csv", ".xml", ".html", ".lang", ".mcmeta", ".sh", ".bat", ".ps1"
    };

    private ServerInstance? _instance;
    private string _relative = "";
    private string? _editingPath;

    public ServerFilePanel() => InitializeComponent();

    public void Show(ServerInstance instance)
    {
        _instance = instance;
        _relative = "";
        PanEditor.Visibility = Visibility.Collapsed;
        _Refresh();
    }

    public void Close()
    {
        _instance = null;
        _editingPath = null;
        PanEditor.Visibility = Visibility.Collapsed;
    }

    private string _CurrentDirectory => _instance is null ? "" : Path.Combine(_instance.DirectoryPath, _relative);

    private void _Refresh()
    {
        if (_instance is null) return;

        PanList.Children.Clear();
        LabPath.Text = _relative.Length == 0 ? _instance.Name : $"{_instance.Name}\\{_relative}";
        BtnUp.IsEnabled = _relative.Length > 0;

        List<string> directories;
        List<string> files;
        try
        {
            var root = _CurrentDirectory;
            directories = [.. Directory.EnumerateDirectories(root)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)];
            files = [.. Directory.EnumerateFiles(root)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModBase.Log(ex, "[Server] 读取服务器目录失败");
            HintService.Hint(Lang.Text("Server.Files.ReadFailed"), HintType.Error);
            return;
        }

        foreach (var directory in directories) PanList.Children.Add(_CreateItem(directory, true));
        foreach (var file in files) PanList.Children.Add(_CreateItem(file, false));
        LabEmpty.Visibility = directories.Count + files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private MyListItem _CreateItem(string path, bool isDirectory)
    {
        var name = Path.GetFileName(path);
        long size = 0;
        if (!isDirectory)
        {
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // 读不到大小就按 0 显示
            }
        }

        var item = new MyListItem
        {
            Title = name,
            Info = isDirectory ? Lang.Text("Server.Files.Folder") : _FormatSize(size),
            SvgIcon = isDirectory ? "lucide/folder-open" : _IconOf(name),
            Height = 42d,
            MinPaddingRight = 105,
            Type = MyListItem.CheckType.Clickable,
            SnapsToDevicePixels = true,
            Tag = path
        };

        // 双击进目录或者开文本 单击只是选中 免得误触
        item.Click += (_, e) =>
        {
            if (e.ClickCount < 2 || _instance is null) return;
            if (isDirectory)
            {
                _relative = Path.GetRelativePath(_instance.DirectoryPath, path);
                _Refresh();
            }
            else if (_IsText(name))
            {
                _OpenEditor(path);
            }
        };

        var buttons = new List<MyIconButton>();

        if (!isDirectory)
        {
            var download = new MyIconButton
            {
                SvgIcon = "lucide/download",
                ToolTip = Lang.Text("Common.Action.Download"),
                LogoScale = 0.85
            };
            download.Click += (_, _) => _Download(path);
            buttons.Add(download);

            if (_IsText(name))
            {
                var edit = new MyIconButton
                {
                    SvgIcon = "lucide/scroll-text",
                    ToolTip = Lang.Text("Server.Files.Edit"),
                    LogoScale = 0.85
                };
                edit.Click += (_, _) => _OpenEditor(path);
                buttons.Add(edit);
            }
        }

        var rename = new MyIconButton
        {
            SvgIcon = "lucide/pencil",
            ToolTip = Lang.Text("Server.Files.Rename"),
            LogoScale = 0.85
        };
        rename.Click += (_, _) => _Rename(path, isDirectory);
        buttons.Add(rename);

        var delete = new MyIconButton
        {
            SvgIcon = "lucide/trash-2",
            ToolTip = Lang.Text("Common.Action.Delete"),
            LogoScale = 0.85
        };
        delete.Click += (_, _) => _Delete(path, name, isDirectory);
        buttons.Add(delete);

        item.Buttons = buttons.ToArray();
        return item;
    }

    #region 工具栏

    private void BtnUp_Click(object sender, MouseButtonEventArgs e)
    {
        if (_relative.Length == 0) return;

        var parent = Path.GetDirectoryName(_relative);
        _relative = parent ?? "";
        _Refresh();
    }

    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e) => _Refresh();

    private void BtnNewFolder_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;

        var name = ModMain.MyMsgBoxInput(Lang.Text("Server.Files.NewFolder"),
            hintText: Lang.Text("Server.Files.NameHint"));
        if (!_AskName(name)) return;

        try
        {
            Directory.CreateDirectory(Path.Combine(_CurrentDirectory, name!.Trim()));
            _Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModBase.Log(ex, "[Server] 新建文件夹失败");
            HintService.Hint(Lang.Text("Server.Files.WriteFailed"), HintType.Error);
        }
    }

    private void BtnUpload_Click(object sender, MouseButtonEventArgs e)
    {
        if (_instance is null) return;

        var dialog = new OpenFileDialog
        {
            Title = Lang.Text("Server.Files.Upload"),
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        var done = 0;
        foreach (var file in dialog.FileNames)
        {
            try
            {
                File.Copy(file, Path.Combine(_CurrentDirectory, Path.GetFileName(file)), true);
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ModBase.Log(ex, $"[Server] 上传文件失败 {file}");
            }
        }

        _Refresh();
        HintService.Hint(done == dialog.FileNames.Length
            ? Lang.Text("Server.Files.Uploaded", done)
            : Lang.Text("Server.Files.WriteFailed"), done == dialog.FileNames.Length ? HintType.Success : HintType.Error);
    }

    #endregion

    #region 单个文件的操作

    private void _Rename(string path, bool isDirectory)
    {
        var oldName = Path.GetFileName(path);
        var name = ModMain.MyMsgBoxInput(Lang.Text("Server.Files.Rename"), "", oldName,
            hintText: Lang.Text("Server.Files.NameHint"));
        if (!_AskName(name) || name!.Trim() == oldName) return;

        try
        {
            var target = Path.Combine(Path.GetDirectoryName(path)!, name.Trim());
            if (isDirectory) Directory.Move(path, target);
            else File.Move(path, target);
            _Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModBase.Log(ex, "[Server] 重命名失败");
            HintService.Hint(Lang.Text("Server.Files.WriteFailed"), HintType.Error);
        }
    }

    private void _Delete(string path, string name, bool isDirectory)
    {
        ModMain.MyMsgBox(Lang.Text("Server.Files.Delete.Confirm", name),
            Lang.Text("Common.Action.Delete"),
            button2: Lang.Text("Common.Action.Cancel"),
            isWarn: true,
            button1Action: () =>
            {
                try
                {
                    if (isDirectory) Directory.Delete(path, true);
                    else File.Delete(path);
                    _Refresh();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ModBase.Log(ex, "[Server] 删除文件失败");
                    HintService.Hint(Lang.Text("Server.Files.WriteFailed"), HintType.Error);
                }
            });
    }

    private void _Download(string path)
    {
        var dialog = new SaveFileDialog
        {
            Title = Lang.Text("Common.Action.Download"),
            FileName = Path.GetFileName(path)
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.Copy(path, dialog.FileName, true);
            HintService.Hint(Lang.Text("Server.Files.Downloaded", dialog.FileName), HintType.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModBase.Log(ex, "[Server] 下载文件失败");
            HintService.Hint(Lang.Text("Server.Files.WriteFailed"), HintType.Error);
        }
    }

    #endregion

    #region 文本编辑

    private void _OpenEditor(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxEditableBytes)
            {
                HintService.Hint(Lang.Text("Server.Files.TooLarge"), HintType.Error);
                return;
            }

            // 认 BOM 同时拒绝非法 UTF-8 二进制按文本读出来再存回去会把文件写坏
            using var reader = new StreamReader(path, new UTF8Encoding(false, true), true);
            var text = reader.ReadToEnd();

            _editingPath = path;
            CardEditor.Title = $"{Lang.Text("Server.Files.Edit")} · {Path.GetFileName(path)}";
            TextEditor.Text = text;
            LabEditor.Text = "";
            PanEditor.Visibility = Visibility.Visible;
        }
        catch (DecoderFallbackException)
        {
            HintService.Hint(Lang.Text("Server.Files.NotText"), HintType.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModBase.Log(ex, "[Server] 打开文件失败");
            HintService.Hint(Lang.Text("Server.Files.ReadFailed"), HintType.Error);
        }
    }

    private void BtnEditorSave_Click(object sender, MouseButtonEventArgs e)
    {
        if (_editingPath is null) return;

        try
        {
            // 不带 BOM 写回去 有些配置解析器见到 BOM 就报错
            File.WriteAllText(_editingPath, TextEditor.Text, new UTF8Encoding(false));
            _editingPath = null;
            PanEditor.Visibility = Visibility.Collapsed;
            _Refresh();
            HintService.Hint(Lang.Text("Server.Files.Saved"), HintType.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModBase.Log(ex, "[Server] 保存文件失败");
            LabEditor.Text = Lang.Text("Server.Files.WriteFailed");
        }
    }

    private void BtnEditorCancel_Click(object sender, MouseButtonEventArgs e)
    {
        _editingPath = null;
        PanEditor.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region 工具

    // 名字只允许在当层目录里用 带分隔符或者 .. 一律拒掉 免得写到目录外面去
    private static bool _AskName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
        {
            HintService.Hint(Lang.Text("Server.Files.NameInvalid"), HintType.Error);
            return false;
        }

        return true;
    }

    private static bool _IsText(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length > 0 && TextExtensions.Contains(extension);
    }

    private static string _IconOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".zip" or ".gz" or ".rar" or ".7z" or ".jar" => "lucide/file-archive",
        ".txt" or ".md" or ".log" or ".json" or ".yml" or ".yaml" or ".properties" or ".toml"
            or ".ini" or ".conf" or ".cfg" => "lucide/file-text",
        _ => "lucide/file-box"
    };

    private static string _FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.#} GB"
    };

    #endregion
}
