using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using Forms = System.Windows.Forms;
using System.Windows.Interop;
using DataObject = System.Windows.DataObject;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace 桌面整理工具
{
    public partial class FenceWindow : Window
    {
        private readonly FenceConfig _config;
        public FenceConfig Config => _config;

        public ObservableCollection<FenceItem> Items { get; } = new ObservableCollection<FenceItem>();

        // 每个分区的专属物理收纳文件夹路径
        private readonly string _partitionFolder;

        // 窗口拖动变量
        private bool _isDragging = false;
        private Point _dragStartPoint;
        private double _startLeft;
        private double _startTop;
        private Point _dragStartMouseScreenPoint;

        // 图标拖拽排序与跨窗口移动变量
        private Point _itemDragStartPoint;
        private FenceItem? _draggedItem = null;
        private bool _isDraggingItem = false;

        public event EventHandler? ConfigChanged;
        public event EventHandler? DeleteRequested;

        private bool _isLocked = false;
        public bool IsLocked
        {
            get => _isLocked;
            set => _isLocked = value;
        }

        private bool _enableBlur = true;
        private bool _isEditMode = false;

        public FenceWindow(FenceConfig config, bool enableBlur, bool isEditMode)
        {
            InitializeComponent();
            _config = config;
            _enableBlur = enableBlur;
            _isEditMode = isEditMode;

            _partitionFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "桌面整理工具",
                "Partitions",
                _config.Id.ToString()
            );

            if (!Directory.Exists(_partitionFolder))
            {
                Directory.CreateDirectory(_partitionFolder);
            }

            // 初始化窗口位置与保存好的尺寸配置，允许自适应或固定用户拉伸的大小
            this.Left = _config.X;
            this.Top = _config.Y;
            if (_config.Width > 0 && _config.Height > 0)
            {
                this.SizeToContent = SizeToContent.Manual;
                this.Width = _config.Width;
                this.Height = _config.Height;
            }
            else
            {
                this.SizeToContent = SizeToContent.WidthAndHeight;
            }
            this.TitleText.Text = _config.Title;

            FileListBox.ItemsSource = Items;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 1. 动态开启或关闭 Windows 的圆角与强制毛玻璃高斯模糊
            Win32Helper.SetBlurBehind(this, _enableBlur);

            // 2. 将顶级无边框悬浮窗钉在桌面上
            IntPtr mainHWnd = new WindowInteropHelper(Application.Current.MainWindow).EnsureHandle();
            Win32Helper.PinToDesktopBackground(this, mainHWnd);

            // 2.5. 挂载 Win32 消息钩子，以在用户拉伸完窗口释放鼠标瞬间触发磁吸自适应对齐
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WndProc);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 3. 扫描物理目录并加载文件
            LoadFiles();

            // 5. 应用全局磨砂效果设置
            ApplyBlurEffect(_enableBlur);

            // 6. 应用全局编辑模式设置
            SetEditMode(_isEditMode);

            // 6.5. 拯救冷启动位置处于屏幕外的幽灵卡片窗口，将其强制拉回并修正保存到配置中
            // 考虑 DPI 缩放换算当前窗口坐标对应的显示器屏幕 (Forms.Screen)
            double dpiScaleX = 1.0;
            double dpiScaleY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            int pixelX = (int)(this.Left * dpiScaleX);
            int pixelY = (int)(this.Top * dpiScaleY);
            var currentScreen = Forms.Screen.FromPoint(new System.Drawing.Point(pixelX, pixelY));

            double workLeft = currentScreen.WorkingArea.X / dpiScaleX;
            double workTop = currentScreen.WorkingArea.Y / dpiScaleY;
            double workWidth = currentScreen.WorkingArea.Width / dpiScaleX;
            double workHeight = currentScreen.WorkingArea.Height / dpiScaleY;

            double targetLeft = Math.Max(workLeft, Math.Min(this.Left, workLeft + workWidth - this.ActualWidth));
            double targetTop = Math.Max(workTop, Math.Min(this.Top, workTop + workHeight - this.ActualHeight));

            if (this.Left != targetLeft || this.Top != targetTop)
            {
                this.Left = targetLeft;
                this.Top = targetTop;
                _config.X = this.Left;
                _config.Y = this.Top;
                TriggerConfigChanged();
            }

            // 7. DPI 物理像素重定位刷新并贴合最底层
            Win32Helper.ForceShowAndBringToTop(this);
        }

        /// <summary>
        /// 动态开启或关闭磨砂效果并调整 XAML 背景透明度
        /// </summary>
        public void ApplyBlurEffect(bool enable)
        {
            _enableBlur = enable;

            // 利用 WCA 未公开接口，强行在窗口底层注入毛玻璃高斯模糊（完全不受系统透明效果开关限制）
            Win32Helper.SetBlurBehind(this, enable);

            // 动态调节卡片背景色，增强视觉质感
            if (MainBorder != null)
            {
                if (enable)
                {
                    // 开启磨砂：最外层 MainBorder 的 Background 必须被强制设为完全透明 (Transparent)！
                    // 这是 Windows 11 DWM 机制的核心技术限制：一旦外层容器有任何自定义背景色，DWM 就会阻断退化为完全不透光的实心灰色石板！
                    // 由于内部的 FileListBox 与 TitleBar 拥有微量背景色 (#01FFFFFF)，因此既能激活完美磨砂，又绝对不会发生鼠标穿透！
                    MainBorder.Background = System.Windows.Media.Brushes.Transparent;
                    MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                }
                else
                {
                    // 关闭磨砂（全透明）：最外层同样设为完全透明，仅保留 40% 透明白描边
                    MainBorder.Background = System.Windows.Media.Brushes.Transparent;
                    MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
                }
            }
        }

        /// <summary>
        /// 动态显示或隐藏分区头部的编辑 (重命名) 与删除按钮，以供托盘“开启编辑模式”联动
        /// </summary>
        public void SetEditMode(bool enable)
        {
            _isEditMode = enable;
            if (RenameButton != null)
            {
                RenameButton.Visibility = enable ? Visibility.Visible : Visibility.Collapsed;
            }
            if (DeleteButton != null)
            {
                DeleteButton.Visibility = enable ? Visibility.Visible : Visibility.Collapsed;
            }

            // 1. 动态切换 WindowChrome 的拉伸宽度 (编辑模式下为 6，非编辑模式下为 0 锁定不可拉伸)
            try
            {
                var chrome = new System.Windows.Shell.WindowChrome
                {
                    CaptionHeight = 0,
                    ResizeBorderThickness = enable ? new Thickness(6) : new Thickness(0),
                    GlassFrameThickness = new Thickness(-1)
                };
                System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);
            }
            catch { }

            // 2. 如果开启编辑模式，我们将 SizeToContent 设为 Manual，使手动拉伸生效
            if (enable)
            {
                // 先固化锁死当前的自适应宽度和高度，防止瞬间收缩为 0x0
                if (this.Width.Equals(double.NaN) || this.Width == 0)
                {
                    this.Width = this.ActualWidth;
                }
                if (this.Height.Equals(double.NaN) || this.Height == 0)
                {
                    this.Height = this.ActualHeight;
                }
                this.SizeToContent = SizeToContent.Manual;
            }
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // 当分区窗口失去焦点时，立刻退回系统最底层，使其完美粘在桌面，不挡住任何其他应用
            IntPtr mainHWnd = new WindowInteropHelper(Application.Current.MainWindow).EnsureHandle();
            Win32Helper.PinToDesktopBackground(this, mainHWnd);
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 只有在开启编辑模式且手动拉伸时，才持久化记录最新的卡片尺寸
            if (_config != null && this.ActualWidth > 0 && this.ActualHeight > 0)
            {
                if (_isEditMode && this.SizeToContent == SizeToContent.Manual)
                {
                    _config.Width = this.ActualWidth;
                    _config.Height = this.ActualHeight;
                    TriggerConfigChanged();
                }
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_EXITSIZEMOVE = 0x0232;
            if (msg == WM_EXITSIZEMOVE)
            {
                if (_isEditMode && this.SizeToContent == SizeToContent.Manual)
                {
                    SnapToGrid();
                }
            }
            return IntPtr.Zero;
        }

        private void SnapToGrid()
        {
            try
            {
                double currentWidth = this.ActualWidth;
                double currentHeight = this.ActualHeight;

                double snapWidth = currentWidth;
                double snapHeight = currentHeight;
                bool xSnapped = false;
                bool ySnapped = false;

                const double NeighborSnapThreshold = 18.0;

                double targetRight = this.Left + currentWidth;
                double targetBottom = this.Top + currentHeight;

                // 1. 尝试磁吸周边其他分区的边界 (优先保证卡片对齐排布)
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is FenceWindow other && other != this && other.Visibility == Visibility.Visible)
                    {
                        double otherLeft = other.Left;
                        double otherRight = otherLeft + other.ActualWidth;
                        double otherTop = other.Top;
                        double otherBottom = otherTop + other.ActualHeight;

                        // 水平拉伸边缘对齐 (Right 与 otherRight 对齐，或者 Right 与 otherLeft 对齐)
                        if (Math.Abs(targetRight - otherRight) < NeighborSnapThreshold)
                        {
                            snapWidth = otherRight - this.Left;
                            xSnapped = true;
                        }
                        else if (Math.Abs(targetRight - otherLeft) < NeighborSnapThreshold)
                        {
                            snapWidth = otherLeft - this.Left;
                            xSnapped = true;
                        }

                        // 垂直拉伸边缘对齐 (Bottom 与 otherBottom 对齐，或者 Bottom 与 otherTop 对齐)
                        if (Math.Abs(targetBottom - otherBottom) < NeighborSnapThreshold)
                        {
                            snapHeight = otherBottom - this.Top;
                            ySnapped = true;
                        }
                        else if (Math.Abs(targetBottom - otherTop) < NeighborSnapThreshold)
                        {
                            snapHeight = otherTop - this.Top;
                            ySnapped = true;
                        }
                    }
                }

                // 2. 如果对应方向没有发生周边吸附，则回退执行内部网格磁吸计算 (包裹图标)
                if (!xSnapped)
                {
                    int cols = (int)Math.Round((currentWidth - 20) / 70.0);
                    cols = Math.Max(2, cols); // 限制宽度最窄为 2 列
                    snapWidth = cols * 70 + 20;
                }

                if (!ySnapped)
                {
                    int rows = (int)Math.Round((currentHeight - 52) / 75.0);
                    rows = Math.Max(1, rows); // 限制高度最矮为 1 行
                    snapHeight = rows * 75 + 52;
                }

                // 3. 磁吸重置尺寸并保存配置
                this.Width = snapWidth;
                this.Height = snapHeight;

                if (_config != null)
                {
                    _config.Width = snapWidth;
                    _config.Height = snapHeight;
                    TriggerConfigChanged();
                }
            }
            catch { }
        }

        private void LoadFiles()
        {
            Items.Clear();
            if (!Directory.Exists(_partitionFolder))
            {
                Directory.CreateDirectory(_partitionFolder);
            }

            // 1. 启动重新收纳机制：扫描桌面，如果发现属于此分区的记忆文件被还原在桌面上，重新剪切收纳进来
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                foreach (var path in _config.FilePaths)
                {
                    string fileName = Path.GetFileName(path);
                    string onDesktop = Path.Combine(desktopPath, fileName);
                    
                    if (File.Exists(onDesktop) || Directory.Exists(onDesktop))
                    {
                        try
                        {
                            string targetInPartition = Path.Combine(_partitionFolder, fileName);
                            if (File.Exists(onDesktop))
                            {
                                File.Move(onDesktop, targetInPartition, true);
                            }
                            else if (Directory.Exists(onDesktop))
                            {
                                Directory.Move(onDesktop, targetInPartition);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"启动重新收纳桌面文件 \"{fileName}\" 失败: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动扫描桌面重新收纳失败: {ex.Message}");
            }

            // 2. 加载分区存储目录下的物理文件并绑定列表
            try
            {
                // 获取物理文件夹下的实际存在文件列表
                var currentPhysicalFiles = new System.Collections.Generic.HashSet<string>(
                    Directory.GetFileSystemEntries(_partitionFolder), 
                    StringComparer.OrdinalIgnoreCase
                );

                // 按照 config.FilePaths 里记忆的排序加载
                var loadedPaths = new System.Collections.Generic.List<string>();
                foreach (var path in _config.FilePaths)
                {
                    string fileName = Path.GetFileName(path);
                    string expectedPath = Path.Combine(_partitionFolder, fileName);

                    if (currentPhysicalFiles.Contains(expectedPath))
                    {
                        var item = CreateFenceItem(expectedPath);
                        Items.Add(item);
                        loadedPaths.Add(expectedPath);
                        currentPhysicalFiles.Remove(expectedPath);
                    }
                }

                // 3. 如果物理文件夹里有多出的文件，则加到末尾加载，保证文件绝不遗漏
                foreach (var path in currentPhysicalFiles)
                {
                    var item = CreateFenceItem(path);
                    Items.Add(item);
                    loadedPaths.Add(path);
                }

                // 同步更新记忆配置列表
                _config.FilePaths.Clear();
                _config.FilePaths.AddRange(loadedPaths);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载分区文件与排序失败: {ex.Message}");
            }
        }

        public void SaveOrderToConfig()
        {
            // 将当前的物理文件顺序同步回 config.FilePaths 列表以供持久化记忆排序
            _config.FilePaths.Clear();
            foreach (var item in Items)
            {
                _config.FilePaths.Add(item.FilePath);
            }
        }

        private FenceItem CreateFenceItem(string path)
        {
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
            {
                name = path;
            }
            else if (Path.HasExtension(path) && !Directory.Exists(path))
            {
                name = Path.GetFileNameWithoutExtension(path);
            }

            return new FenceItem
            {
                FilePath = path,
                FileName = name,
                Icon = Win32Helper.GetFileIcon(path, large: true)
            };
        }

        private void TriggerConfigChanged()
        {
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        #region Window Dragging (拖动窗口)

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isLocked) return;

            _isDragging = true;
            _dragStartPoint = e.GetPosition(this);
            _startLeft = this.Left;
            _startTop = this.Top;
            
            // 记录鼠标按下瞬间的屏幕绝对物理坐标，用以支持高精度物理磁吸拖动
            _dragStartMouseScreenPoint = this.PointToScreen(_dragStartPoint);

            TitleBar.CaptureMouse();
            e.Handled = true;
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                // 获取当前鼠标绝对物理坐标
                Point currentMousePoint = this.PointToScreen(e.GetPosition(this));

                // 考虑屏幕 DPI 缩放比例，转换物理位移差值为逻辑位移差值
                double dpiScaleX = 1.0;
                double dpiScaleY = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                    dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
                }

                double totalDeltaX = (currentMousePoint.X - _dragStartMouseScreenPoint.X) / dpiScaleX;
                double totalDeltaY = (currentMousePoint.Y - _dragStartMouseScreenPoint.Y) / dpiScaleY;

                // 1. 计算候选移动位置 (未施加磁吸前的默认平移位置)
                double candidateLeft = _startLeft + totalDeltaX;
                double candidateTop = _startTop + totalDeltaY;
                double candidateRight = candidateLeft + this.ActualWidth;
                double candidateBottom = candidateTop + this.ActualHeight;

                double finalLeft = candidateLeft;
                double finalTop = candidateTop;

                // 磁吸触发阈值：当边界距离小于 18 像素时自动吸附对齐
                const double SnapThreshold = 18.0; 

                // 2. 磁吸当前鼠标所在屏幕的工作区边界 (DPI 感知，完美支持多显示器拖拽)
                var currentScreen = Forms.Screen.FromPoint(new System.Drawing.Point((int)currentMousePoint.X, (int)currentMousePoint.Y));
                double workLeft = currentScreen.WorkingArea.X / dpiScaleX;
                double workTop = currentScreen.WorkingArea.Y / dpiScaleY;
                double workWidth = currentScreen.WorkingArea.Width / dpiScaleX;
                double workHeight = currentScreen.WorkingArea.Height / dpiScaleY;

                // 屏幕左边缘吸附
                if (Math.Abs(candidateLeft - workLeft) < SnapThreshold)
                {
                    finalLeft = workLeft;
                }
                // 屏幕右边缘吸附
                else if (Math.Abs(candidateRight - (workLeft + workWidth)) < SnapThreshold)
                {
                    finalLeft = workLeft + workWidth - this.ActualWidth;
                }

                // 屏幕上边缘吸附
                if (Math.Abs(candidateTop - workTop) < SnapThreshold)
                {
                    finalTop = workTop;
                }
                // 屏幕下边缘吸附
                else if (Math.Abs(candidateBottom - (workTop + workHeight)) < SnapThreshold)
                {
                    finalTop = workTop + workHeight - this.ActualHeight;
                }

                // 3. 磁吸其他活跃分区卡片（对齐/贴合对齐，挣脱手感极佳）
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is FenceWindow other && other != this && other.Visibility == Visibility.Visible)
                    {
                        double otherLeft = other.Left;
                        double otherRight = otherLeft + other.ActualWidth;
                        double otherTop = other.Top;
                        double otherBottom = otherTop + other.ActualHeight;

                        // --- 水平方向 (X 轴) 磁吸对齐 ---
                        // A 左边 贴 B 右边 (并排排列)
                        if (Math.Abs(candidateLeft - otherRight) < SnapThreshold)
                        {
                            finalLeft = otherRight;
                        }
                        // A 右边 贴 B 左边 (并排排列)
                        else if (Math.Abs(candidateRight - otherLeft) < SnapThreshold)
                        {
                            finalLeft = otherLeft - this.ActualWidth;
                        }
                        // A 左边 对齐 B 左边 (垂直对齐)
                        else if (Math.Abs(candidateLeft - otherLeft) < SnapThreshold)
                        {
                            finalLeft = otherLeft;
                        }
                        // A 右边 对齐 B 右边 (垂直对齐)
                        else if (Math.Abs(candidateRight - otherRight) < SnapThreshold)
                        {
                            finalLeft = otherRight - this.ActualWidth;
                        }

                        // --- 垂直方向 (Y 轴) 磁吸对齐 ---
                        // A 上边 贴 B 下边 (上下层叠排列)
                        if (Math.Abs(candidateTop - otherBottom) < SnapThreshold)
                        {
                            finalTop = otherBottom;
                        }
                        // A 下边 贴 B 上边 (上下层叠排列)
                        else if (Math.Abs(candidateBottom - otherTop) < SnapThreshold)
                        {
                            finalTop = otherTop - this.ActualHeight;
                        }
                        // A 上边 对齐 B 上边 (水平对齐)
                        else if (Math.Abs(candidateTop - otherTop) < SnapThreshold)
                        {
                            finalTop = otherTop;
                        }
                        // A 下边 对齐 B 下边 (水平对齐)
                        else if (Math.Abs(candidateBottom - otherBottom) < SnapThreshold)
                        {
                            finalTop = otherBottom - this.ActualHeight;
                        }
                    }
                }

                // 3.5. 实时物理碰撞阻挡：检测是否与任何其他活跃分区重叠，并计算最小侵入推开位移，不让卡片互相压过重叠
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is FenceWindow other && other != this && other.Visibility == Visibility.Visible)
                    {
                        double otherLeft = other.Left;
                        double otherRight = otherLeft + other.ActualWidth;
                        double otherTop = other.Top;
                        double otherBottom = otherTop + other.ActualHeight;

                        // 检测是否相交 (相交重叠则执行 MTV 阻挡推开)
                        if (!(finalLeft + this.ActualWidth <= otherLeft + 1 || otherRight <= finalLeft + 1 ||
                              finalTop + this.ActualHeight <= otherTop + 1 || otherBottom <= finalTop + 1))
                        {
                            // 计算四个脱离方向的位移
                            double shiftLeft = otherLeft - this.ActualWidth - 2;
                            double shiftRight = otherRight + 2;
                            double shiftTop = otherTop - this.ActualHeight - 2;
                            double shiftBottom = otherBottom + 2;

                            double distLeft = Math.Abs(shiftLeft - candidateLeft);
                            double distRight = Math.Abs(shiftRight - candidateLeft);
                            double distTop = Math.Abs(shiftTop - candidateTop);
                            double distBottom = Math.Abs(shiftBottom - candidateTop);

                            // 寻找哪种推开距离最小（最符合鼠标运动轨迹）
                            double minDist = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));

                            if (minDist == distLeft)
                            {
                                finalLeft = shiftLeft;
                            }
                            else if (minDist == distRight)
                            {
                                finalLeft = shiftRight;
                            }
                            else if (minDist == distTop)
                            {
                                finalTop = shiftTop;
                            }
                            else
                            {
                                finalTop = shiftBottom;
                            }
                        }
                    }
                }

                // 3.9. 屏幕工作区强力限位（防止分区被拖拽移出屏幕可见范围外）
                finalLeft = Math.Max(workLeft, Math.Min(finalLeft, workLeft + workWidth - this.ActualWidth));
                finalTop = Math.Max(workTop, Math.Min(finalTop, workTop + workHeight - this.ActualHeight));

                // 4. 应用计算出的绝对磁吸与碰撞阻挡后坐标并存入配置
                this.Left = finalLeft;
                this.Top = finalTop;

                _config.X = this.Left;
                _config.Y = this.Top;
            }
        }

        private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                TitleBar.ReleaseMouseCapture();

                // 5. 松手安全重叠回弹兜底
                bool overlapped = false;
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is FenceWindow other && other != this && other.Visibility == Visibility.Visible)
                    {
                        double otherLeft = other.Left;
                        double otherRight = otherLeft + other.ActualWidth;
                        double otherTop = other.Top;
                        double otherBottom = otherTop + other.ActualHeight;

                        if (!(this.Left + this.ActualWidth <= otherLeft + 1 || otherRight <= this.Left + 1 ||
                              this.Top + this.ActualHeight <= otherTop + 1 || otherBottom <= this.Top + 1))
                        {
                            overlapped = true;
                            break;
                        }
                    }
                }

                if (overlapped)
                {
                    // 发生了意外的重叠穿透，自动回弹退回移动前出发点，确保绝对 100% 零重叠！
                    this.Left = _startLeft;
                    this.Top = _startTop;
                    _config.X = _startLeft;
                    _config.Y = _startTop;
                }

                TriggerConfigChanged();
            }
        }
        #endregion

        #region Title Editing (重命名)

        private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isEditMode && e.ClickCount == 2)
            {
                StartRename();
                e.Handled = true;
            }
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isEditMode)
            {
                StartRename();
            }
        }

        private void StartRename()
        {
            TitleText.Visibility = Visibility.Collapsed;
            TitleEditBox.Text = TitleText.Text;
            TitleEditBox.Visibility = Visibility.Visible;
            TitleEditBox.Focus();
            TitleEditBox.SelectAll();
        }

        private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
        {
            EndRename(commit: true);
        }

        private void TitleEditBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                EndRename(commit: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndRename(commit: false);
                e.Handled = true;
            }
        }

        private void EndRename(bool commit)
        {
            if (TitleEditBox.Visibility != Visibility.Visible) return;

            if (commit)
            {
                string newTitle = TitleEditBox.Text.Trim();
                if (!string.IsNullOrEmpty(newTitle))
                {
                    TitleText.Text = newTitle;
                    _config.Title = newTitle;
                    TriggerConfigChanged();
                }
            }

            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
        }

        #endregion

        #region File Drag/Drop Reorder & Movement (物理排序、跨分区拖拽与物理收纳)

        private void FileListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _draggedItem = null;
            _isDraggingItem = false;

            // 查找按下的 ListBoxItem 项
            var element = FileListBox.InputHitTest(e.GetPosition(FileListBox)) as DependencyObject;
            var listBoxItem = FindParent<ListBoxItem>(element);
            
            if (listBoxItem != null && listBoxItem.DataContext is FenceItem item)
            {
                _draggedItem = item;
                _itemDragStartPoint = e.GetPosition(FileListBox);
            }
        }

        private void FileListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedItem != null && !_isDraggingItem)
            {
                Point position = e.GetPosition(FileListBox);
                if (Math.Abs(position.X - _itemDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(position.Y - _itemDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isDraggingItem = true;

                    // 1. 封装多数据格式的 DataObject
                    DataObject data = new DataObject();
                    // 放入自定义类型以供软件内部排序或跨分区拖拽识别
                    data.SetData(typeof(FenceItem), _draggedItem);
                    // 放入标准的系统文件列表格式，以便能被 Windows 桌面或资源管理器接收并处理物理移动
                    string[] filePaths = new string[] { _draggedItem.FilePath };
                    data.SetData(DataFormats.FileDrop, filePaths);

                    // 2. 发起拖拽，支持 Move 和 Copy
                    DragDropEffects result = DragDrop.DoDragDrop(FileListBox, data, DragDropEffects.Copy | DragDropEffects.Move);

                    // 3. 拖拽操作结束，检查该物理文件是否被系统移动剪切走（如用户将其拖拽到了系统桌面或资源管理器）
                    // 只要物理文件夹内对应的文件被移动不存在了，就从视图列表中移除，由 SizeToContent 自动回弹收缩窗口
                    if (result == DragDropEffects.Move)
                    {
                        if (!File.Exists(_draggedItem.FilePath) && !Directory.Exists(_draggedItem.FilePath))
                        {
                            Items.Remove(_draggedItem);
                            SaveOrderToConfig();
                            TriggerConfigChanged();
                        }
                    }

                    _draggedItem = null;
                    _isDraggingItem = false;
                }
            }
        }

        private void FileListBox_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(FenceItem)) || e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void FileListBox_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(FenceItem)) || e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void FileListBox_Drop(object sender, DragEventArgs e)
        {
            // 1. 处理本软件项的排序或跨窗体转移
            if (e.Data.GetDataPresent(typeof(FenceItem)))
            {
                var draggedItem = e.Data.GetData(typeof(FenceItem)) as FenceItem;
                if (draggedItem != null)
                {
                    // 找到释放坐标位置下的目标项
                    Point position = e.GetPosition(FileListBox);
                    var element = FileListBox.InputHitTest(position) as DependencyObject;
                    var targetItem = FindParent<ListBoxItem>(element);
                    
                    int targetIndex = Items.Count;
                    if (targetItem != null && targetItem.DataContext is FenceItem targetData)
                    {
                        targetIndex = Items.IndexOf(targetData);
                    }

                    if (Items.Contains(draggedItem))
                    {
                        // 本分区内拖动排序
                        int sourceIndex = Items.IndexOf(draggedItem);
                        if (sourceIndex != targetIndex && targetIndex >= 0 && targetIndex < Items.Count)
                        {
                            Items.Move(sourceIndex, targetIndex);
                        }
                    }
                    else
                    {
                        // 跨分区拖拽转移物理文件！
                        try
                        {
                            string targetPath = Path.Combine(_partitionFolder, Path.GetFileName(draggedItem.FilePath));
                            
                            // 物理转移
                            if (File.Exists(draggedItem.FilePath))
                            {
                                File.Move(draggedItem.FilePath, targetPath, true);
                            }
                            else if (Directory.Exists(draggedItem.FilePath))
                            {
                                Directory.Move(draggedItem.FilePath, targetPath);
                            }

                            // 从原先的分区窗口的 Items 中剔除
                            foreach (Window w in Application.Current.Windows)
                            {
                                if (w is FenceWindow otherWin && otherWin != this)
                                {
                                    if (otherWin.Items.Contains(draggedItem))
                                    {
                                        otherWin.Items.Remove(draggedItem);
                                        otherWin.SaveOrderToConfig();
                                    }
                                }
                            }

                            // 插入到本分区相应位置
                            draggedItem.FilePath = targetPath;
                            if (targetIndex >= 0 && targetIndex <= Items.Count)
                            {
                                Items.Insert(targetIndex, draggedItem);
                            }
                            else
                            {
                                Items.Add(draggedItem);
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"跨分区转移图标失败: {ex.Message}", "转移失败", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }

                    // 持久化排序配置
                    SaveOrderToConfig();
                    TriggerConfigChanged();
                }
            }
            // 2. 处理外部拖入的文件 (如桌面上的图标)
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                bool changed = false;

                // 计算落点插入下标
                Point position = e.GetPosition(FileListBox);
                var element = FileListBox.InputHitTest(position) as DependencyObject;
                var targetItem = FindParent<ListBoxItem>(element);
                
                int targetIndex = Items.Count;
                if (targetItem != null && targetItem.DataContext is FenceItem targetData)
                {
                    targetIndex = Items.IndexOf(targetData);
                }

                foreach (var file in files)
                {
                    if (File.Exists(file) || Directory.Exists(file))
                    {
                        try
                        {
                            string targetPath = Path.Combine(_partitionFolder, Path.GetFileName(file));
                            if (file != targetPath)
                            {
                                // 执行物理移动收纳
                                if (File.Exists(file))
                                {
                                    File.Move(file, targetPath, true);
                                }
                                else if (Directory.Exists(file))
                                {
                                    Directory.Move(file, targetPath);
                                }
                                // 后台异步物理复制备份拖入的文件
                                BackupFileAsync(targetPath);

                                var item = CreateFenceItem(targetPath);
                                if (targetIndex >= 0 && targetIndex <= Items.Count)
                                {
                                    Items.Insert(targetIndex, item);
                                    targetIndex++;
                                }
                                else
                                {
                                    Items.Add(item);
                                }
                                changed = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"收纳图标失败: {ex.Message}", "物理整理失败", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }

                if (changed)
                {
                    SaveOrderToConfig();
                    TriggerConfigChanged();
                }
            }
            e.Handled = true;
        }

        private void FileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileListBox.SelectedItem is FenceItem selectedItem)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = selectedItem.FilePath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法打开文件/文件夹: {ex.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void FileListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                if (FileListBox.SelectedItem is FenceItem selectedItem)
                {
                    try
                    {
                        // 还原文件到系统桌面
                        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                        string uniquePath = GetUniqueFilePath(desktopPath, Path.GetFileName(selectedItem.FilePath));

                        if (File.Exists(selectedItem.FilePath))
                        {
                            File.Move(selectedItem.FilePath, uniquePath);
                        }
                        else if (Directory.Exists(selectedItem.FilePath))
                        {
                            Directory.Move(selectedItem.FilePath, uniquePath);
                        }

                        Items.Remove(selectedItem);
                        
                        // 重新排列持久化并更新高度
                        SaveOrderToConfig();
                        TriggerConfigChanged();

                        // 强制刷新外壳，让回退到桌面的图标立刻重绘显现
                        Win32Helper.RefreshDesktop();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"将文件还原到桌面失败: {ex.Message}", "还原失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    e.Handled = true;
                }
            }
        }

        #endregion

        #region Helper Functions (辅助寻找父节点与防重名)

        private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
        {
            if (child == null) return null;
            
            DependencyObject? parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;

            if (parentObject is T parent)
            {
                return parent;
            }
            
            return FindParent<T>(parentObject);
        }

        private string GetUniqueFilePath(string folder, string fileName)
        {
            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            string targetPath = Path.Combine(folder, fileName);
            int count = 1;

            while (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                targetPath = Path.Combine(folder, $"{nameOnly}({count}){extension}");
                count++;
            }
            return targetPath;
        }

        #endregion

        #region Partition Actions (删除分区)

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show($"确定要删除分区 \"{_config.Title}\" 吗？\n(这不会删除收纳在内的物理文件，我们将安全地把所有收纳图标全部退回到系统桌面上)", "删除分区确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                try
                {
                    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    if (Directory.Exists(_partitionFolder))
                    {
                        var files = Directory.GetFileSystemEntries(_partitionFolder);
                        bool allMoved = true;

                        foreach (var file in files)
                        {
                            try
                            {
                                string uniquePath = GetUniqueFilePath(desktopPath, Path.GetFileName(file));
                                if (File.Exists(file))
                                {
                                    File.Move(file, uniquePath);
                                }
                                else if (Directory.Exists(file))
                                {
                                    Directory.Move(file, uniquePath);
                                }
                            }
                            catch (Exception ex)
                            {
                                allMoved = false;
                                MessageBox.Show($"文件 \"{Path.GetFileName(file)}\" 正在被其他程序占用，无法安全移动回桌面。\n原因: {ex.Message}\n为了保障您的数据安全，分区删除操作已被中止，未完成移动的文件仍安全保存在分区中。", "数据保护拦截", MessageBoxButton.OK, MessageBoxImage.Warning);
                                break;
                            }
                        }

                        // 只有在所有文件均成功转移至桌面后，才允许删除该分区的专属物理夹
                        if (allMoved)
                        {
                            Directory.Delete(_partitionFolder, true);
                            
                            // 通知资源管理器立刻重绘，强制桌面图标实时刷出
                            Win32Helper.RefreshDesktop();

                            DeleteRequested?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    else
                    {
                        DeleteRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"删除分区时发生意外错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        #endregion

        #region 退出恢复桌面与后台物理备份

        /// <summary>
        /// 程序退出前，将当前收纳在分区内的所有物理文件全部安全还原移回系统桌面
        /// </summary>
        public void RestoreAllItemsToDesktopBeforeExit()
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                foreach (var item in Items)
                {
                    if (File.Exists(item.FilePath) || Directory.Exists(item.FilePath))
                    {
                        try
                        {
                            string uniquePath = GetUniqueFilePath(desktopPath, Path.GetFileName(item.FilePath));
                            if (File.Exists(item.FilePath))
                            {
                                File.Move(item.FilePath, uniquePath);
                            }
                            else if (Directory.Exists(item.FilePath))
                            {
                                Directory.Move(item.FilePath, uniquePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"退出还原文件 \"{item.FilePath}\" 失败: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"还原全部文件到桌面失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 后台异步物理复制备份拖入的文件/文件夹到当前项目下的“桌面备份”根目录，防灾备份双保险
        /// </summary>
        private void BackupFileAsync(string sourcePath)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string backupDir = @"c:\dev\桌面整理工具\桌面备份";
                    if (!Directory.Exists(backupDir))
                    {
                        Directory.CreateDirectory(backupDir);
                    }

                    string destPath = Path.Combine(backupDir, Path.GetFileName(sourcePath));
                    
                    if (File.Exists(sourcePath))
                    {
                        File.Copy(sourcePath, destPath, true);
                    }
                    else if (Directory.Exists(sourcePath))
                    {
                        CopyDirectory(sourcePath, destPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"后台备份文件失败: {ex.Message}");
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
