using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.IO;
using System.Text.Json;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using System.Diagnostics;

namespace 桌面整理工具
{
    public partial class MainWindow : Window
    {
        private static Mutex? _mutex;
        private readonly List<FenceWindow> _activeWindows = new List<FenceWindow>();
        private Forms.NotifyIcon? _notifyIcon;
        private bool _isLocked = false;
        private bool _allHidden = false;

        // 全局磨砂效果控制变量
        private bool _enableBlur = true;
        private bool _isEditMode = false;
        private Guid _lastDesktopId = Guid.Empty;

        public MainWindow()
        {
            // 单例检测，防止重复启动，使用 Local 前缀以避免普通权限运行由于 Global 前缀导致 UnauthorizedAccessException 异常崩溃。
            _mutex = new Mutex(true, "Local\\DesktopOrganizerFencesAppMutex", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("桌面整理工具已在运行中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
                return;
            }

            InitializeComponent();

            // 1. 加载全局系统设置
            LoadGlobalSettings();

            // 1.5. 启动时自动把桌面上所有的图标全量物理复制备份一份至当前项目下的“桌面备份”中，防灾双保险
            BackupAllDesktopIconsStartup();

            // 2. 初始化托盘与显示分区
            InitTray();
            LoadFences();

            // 3. 注册进程退出监听，确保温和关机、任务管理器结束任务等场景下完璧归赵移回桌面
            AppDomain.CurrentDomain.ProcessExit += (s, e) => ExitApp();

            // 4. 初始化多虚拟桌面跟随定时器 (500毫秒轮询一次，极其低耗，DWM 级别跟随)
            var dispatcherTimer = new System.Windows.Threading.DispatcherTimer();
            dispatcherTimer.Tick += (s, e) => {
                Guid currentDesktopId = Win32Helper.GetCurrentVirtualDesktopId();
                if (currentDesktopId != Guid.Empty && currentDesktopId != _lastDesktopId)
                {
                    // 在第一次启动或虚拟桌面发生真实切换时，对所有窗口执行 Hide-Show 重生，使其完美出现在新虚拟桌面上
                    bool isInitial = (_lastDesktopId == Guid.Empty);
                    _lastDesktopId = currentDesktopId;

                    if (!isInitial)
                    {
                        foreach (var win in _activeWindows)
                        {
                            if (win.IsLoaded && win.Visibility == Visibility.Visible)
                            {
                                try
                                {
                                    // 重生动作：临时隐藏并再次展现以促使 DWM 重绘关联到新桌面，同时重新置底
                                    win.Hide();
                                    win.Show();
                                    Win32Helper.PinToDesktopBackground(win, IntPtr.Zero);
                                    Win32Helper.ForceShowAndBringToTop(win);
                                }
                                catch { }
                            }
                        }
                    }
                }

                // 即使桌面没变，每次 Tick 也对所有分区执行一次跟随同步作为兜底
                foreach (var win in _activeWindows)
                {
                    if (win.IsLoaded && win.Visibility == Visibility.Visible)
                    {
                        Win32Helper.SyncWindowToCurrentVirtualDesktop(win);
                    }
                }
            };
            dispatcherTimer.Interval = TimeSpan.FromMilliseconds(500);
            dispatcherTimer.Start();
        }

        private void InitTray()
        {
            _notifyIcon = new Forms.NotifyIcon
            {
                Text = "桌面整理工具",
                Visible = true
            };

            // 创建漂亮的动态几何 Icon 避免引入外部 ico 文件依赖
            _notifyIcon.Icon = CreateDynamicIcon();

            // 双击托盘显示/隐藏全部
            _notifyIcon.DoubleClick += (s, e) => ToggleAllFences();

            // 创建托盘右键菜单
            var contextMenu = new Forms.ContextMenuStrip();
            
            var addBtn = new Forms.ToolStripMenuItem("新建分区");
            addBtn.Click += (s, e) => AddNewFence();
            contextMenu.Items.Add(addBtn);

            var lockBtn = new Forms.ToolStripMenuItem("锁定分区位置") { Checked = _isLocked };
            lockBtn.Click += (s, e) =>
            {
                _isLocked = !_isLocked;
                lockBtn.Checked = _isLocked;
                foreach (var win in _activeWindows)
                {
                    win.IsLocked = _isLocked;
                }
            };
            contextMenu.Items.Add(lockBtn);

            // 磨砂玻璃特效开关菜单项
            var blurBtn = new Forms.ToolStripMenuItem("开启磨砂玻璃效果") { Checked = _enableBlur };
            blurBtn.Click += (s, e) =>
            {
                _enableBlur = !_enableBlur;
                blurBtn.Checked = _enableBlur;
                SaveGlobalSettings();
                
                // 动态通知所有分区卡片切换磨砂模糊状态
                foreach (var win in _activeWindows)
                {
                    win.ApplyBlurEffect(_enableBlur);
                }
            };
            contextMenu.Items.Add(blurBtn);

            // 开启编辑模式开关菜单项
            var editBtn = new Forms.ToolStripMenuItem("开启编辑模式") { Checked = _isEditMode };
            editBtn.Click += (s, e) =>
            {
                _isEditMode = !_isEditMode;
                editBtn.Checked = _isEditMode;
                SaveGlobalSettings();
                
                // 动态通知所有分区卡片切换编辑模式（控制重命名与删除按钮的显隐）
                foreach (var win in _activeWindows)
                {
                    win.SetEditMode(_isEditMode);
                }
            };
            contextMenu.Items.Add(editBtn);

            contextMenu.Items.Add(new Forms.ToolStripSeparator());

            var hideBtn = new Forms.ToolStripMenuItem("显示/隐藏全部分区");
            hideBtn.Click += (s, e) => ToggleAllFences();
            contextMenu.Items.Add(hideBtn);

            contextMenu.Items.Add(new Forms.ToolStripSeparator());

            var exitBtn = new Forms.ToolStripMenuItem("退出");
            exitBtn.Click += (s, e) => ExitApp();
            contextMenu.Items.Add(exitBtn);

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private System.Drawing.Icon CreateDynamicIcon()
        {
            // 动态绘制一个 32x32 的微型矢量图标，由四个淡蓝色的方块组成，代表桌面分区整理
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    // 绘制现代 Fluent 风格的亮蓝色几何图标
                    using (var brush = new SolidBrush(Color.FromArgb(230, 0, 150, 255)))
                    {
                        g.FillRectangle(brush, 2, 2, 11, 11);
                        g.FillRectangle(brush, 17, 2, 11, 11);
                        g.FillRectangle(brush, 2, 17, 11, 11);
                        g.FillRectangle(brush, 17, 17, 11, 11);
                    }
                }
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void LoadFences()
        {
            var configs = FenceConfigManager.LoadConfigs();
            foreach (var config in configs)
            {
                CreateAndShowFence(config);
            }
        }

        private void CreateAndShowFence(FenceConfig config)
        {
            // 实例化分区窗口，将全局磨砂开关与编辑模式状态作为参数传入
            var win = new FenceWindow(config, _enableBlur, _isEditMode)
            {
                IsLocked = _isLocked
            };

            win.ConfigChanged += (s, e) => SaveAllConfigs();
            win.DeleteRequested += (s, e) =>
            {
                win.Close();
                _activeWindows.Remove(win);
                SaveAllConfigs();
            };

            _activeWindows.Add(win);
            win.Show();
        }

        private void AddNewFence()
        {
            // 默认在屏幕中心生成一个新分区
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;

            var newConfig = new FenceConfig
            {
                Id = Guid.NewGuid(),
                Title = "未命名分区",
                X = (screenWidth - 260) / 2,
                Y = (screenHeight - 150) / 2,
                Width = 260,
                Height = 150,
                FilePaths = new List<string>()
            };

            CreateAndShowFence(newConfig);
            SaveAllConfigs();
        }

        private void SaveAllConfigs()
        {
            var configs = _activeWindows.Select(w => w.Config).ToList();
            FenceConfigManager.SaveConfigs(configs);
        }

        private void ToggleAllFences()
        {
            _allHidden = !_allHidden;
            foreach (var win in _activeWindows)
            {
                if (_allHidden)
                {
                    win.Hide();
                }
                else
                {
                    win.Show();
                }
            }
        }

        private bool _hasExited = false;
        private void ExitApp()
        {
            if (_hasExited) return;
            _hasExited = true;

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            // 退出前，遍历所有分区，强制将物理图标无缝安全移回系统桌面
            foreach (var win in _activeWindows)
            {
                try
                {
                    win.RestoreAllItemsToDesktopBeforeExit();
                    win.Close();
                }
                catch { }
            }

            // 通知资源管理器立刻重绘，让回退到桌面的所有图标在 0.1 秒内实时全部蹦出来！
            Win32Helper.RefreshDesktop();

            try
            {
                Application.Current.Shutdown();
            }
            catch { }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            ExitApp();
        }

        #region 全局设置（磨砂状态与编辑模式）加载与保存

        private void LoadGlobalSettings()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "桌面整理工具");
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string path = Path.Combine(folder, "global_settings.json");
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<GlobalSettings>(json);
                    _enableBlur = settings?.EnableBlur ?? true;
                    _isEditMode = settings?.EnableEditMode ?? false;
                }
            }
            catch
            {
                _enableBlur = true;
                _isEditMode = false;
            }
        }

        private void SaveGlobalSettings()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "桌面整理工具");
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string path = Path.Combine(folder, "global_settings.json");
                var settings = new GlobalSettings 
                { 
                    EnableBlur = _enableBlur,
                    EnableEditMode = _isEditMode
                };
                string json = JsonSerializer.Serialize(settings);
                File.WriteAllText(path, json);
            }
            catch
            {
            }
        }

        public class GlobalSettings
        {
            public bool EnableBlur { get; set; } = true;
            public bool EnableEditMode { get; set; } = false;
        }

        #endregion

        #region 全桌面图标开局安全大备份

        private void BackupAllDesktopIconsStartup()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    string backupDir = @"c:\dev\桌面整理工具\桌面备份";
                    
                    if (!Directory.Exists(backupDir))
                    {
                        Directory.CreateDirectory(backupDir);
                    }

                    var entries = Directory.GetFileSystemEntries(desktopPath);
                    foreach (var entry in entries)
                    {
                        string fileName = Path.GetFileName(entry);
                        if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                        string destPath = Path.Combine(backupDir, fileName);
                        try
                        {
                            if (File.Exists(entry))
                            {
                                File.Copy(entry, destPath, true);
                            }
                            else if (Directory.Exists(entry))
                            {
                                CopyDirectory(entry, destPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"开局备份桌面图标 \"{fileName}\" 失败: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"开局全桌面备份失败: {ex.Message}");
                }
            });
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);
            foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourceDir, destinationDir));
            }
            foreach (string newPath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                File.Copy(newPath, newPath.Replace(sourceDir, destinationDir), true);
            }
        }

        #endregion
    }
}