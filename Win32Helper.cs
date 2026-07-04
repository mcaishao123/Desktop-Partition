using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace 桌面整理工具
{
    public static class Win32Helper
    {
        #region Win32 APIs

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("user32.dll")]
        public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        #endregion

        #region Structs

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        #endregion

        #region Constants

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_CHILD = 0x40000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        // DWM attributes for Windows 11
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        // DWM window corner preferences
        public const int DWMWCP_DEFAULT = 0;
        public const int DWMWCP_DONOTROUND = 1;
        public const int DWMWCP_ROUND = 2;
        public const int DWMWCP_ROUNDSMALL = 3;

        // DWM backdrop types
        public const int DWMSBT_AUTO = 0;
        public const int DWMSBT_DISABLE = 1;
        public const int DWMSBT_MICA = 2;
        public const int DWMSBT_TRANSIENT_BACKDROP = 3; // Acrylic
        public const int DWMSBT_TABBED_BACKDROP = 4;

        // SHGetFileInfo flags
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000; // 32x32
        private const uint SHGFI_SMALLICON = 0x000000001; // 16x16
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        // SetWindowPos flags & handles
        public static readonly IntPtr HWND_TOP = new IntPtr(0);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_SHOWWINDOW = 0x0040;

        #endregion

        /// <summary>
        /// 将顶级无边框窗口置于所有窗口的最底层（贴合桌面背景），并将其 Win32 Owner 设为指定窗口句柄
        /// </summary>
        public static void PinToDesktopBackground(Window window, IntPtr ownerHWnd)
        {
            IntPtr hWnd = new WindowInteropHelper(window).EnsureHandle();
            if (hWnd == IntPtr.Zero) return;

            if (ownerHWnd != IntPtr.Zero)
            {
                // 将窗口的 Win32 Owner 设为指定句柄（解决 Win+D 被最小化隐藏，且避免 OLE 拖放被 Progman 隔离拦截）
                SetWindowLong(hWnd, -8, (int)ownerHWnd);
            }

            // 强制将窗口置于所有普通窗口的最底层（HWND_BOTTOM = 1）
            SetWindowPos(hWnd, new IntPtr(1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }

        /// <summary>
        /// 动态开启或关闭 Windows 11 的系统级背景亚克力（磨砂玻璃）模糊，并开启圆角
        /// </summary>
        public static void SetWin11Backdrop(Window window, bool enableAcrylic)
        {
            IntPtr hWnd = new WindowInteropHelper(window).EnsureHandle();
            if (hWnd == IntPtr.Zero) return;

            // 1. 设置系统背景模糊
            int backdropType = enableAcrylic ? DWMSBT_TRANSIENT_BACKDROP : DWMSBT_AUTO;
            DwmSetWindowAttribute(hWnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

            // 2. 开启圆角效果 (DWMWCP_ROUND = 2)
            int cornerPreference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
        }

        /// <summary>
        /// 利用 Windows 未公开私有 API SetWindowCompositionAttribute 强行在窗口底层实现毛玻璃高斯模糊（完全不受系统透明度开关限制）
        /// </summary>
        public static void SetBlurBehind(Window window, bool enable)
        {
            IntPtr hWnd = new WindowInteropHelper(window).EnsureHandle();
            if (hWnd == IntPtr.Zero) return;

            AccentPolicy policy = new AccentPolicy();
            if (enable)
            {
                // ACCENT_ENABLE_BLURBEHIND = 3。强行在 DWM 层面渲染窗口背景高斯模糊
                policy.AccentState = 3;
                
                // 混合背景颜色色调，在模糊基础上铺上一层若有若无的清亮白（AABBGGRR 格式：0x1EFFFFFF 代表 11% 透明亮白）
                policy.GradientColor = 0x1EFFFFFF;
            }
            else
            {
                // ACCENT_DISABLED = 0。关闭模糊，恢复纯净透明
                policy.AccentState = 0;
            }

            int size = Marshal.SizeOf(policy);
            IntPtr policyPtr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(policy, policyPtr, false);

            WindowCompositionAttributeData data = new WindowCompositionAttributeData
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                Data = policyPtr,
                SizeOfData = size
            };

            SetWindowCompositionAttribute(hWnd, ref data);
            Marshal.FreeHGlobal(policyPtr);
        }

        /// <summary>
        /// 获取文件或文件夹的系统图标
        /// </summary>
        public static ImageSource? GetFileIcon(string path, bool large = true)
        {
            if (string.IsNullOrEmpty(path)) return null;

            SHFILEINFO shfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);

            // 如果文件不存在，可以尝试使用 USEFILEATTRIBUTES 标志获取默认图标
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
            }

            IntPtr res = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), flags);

            if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    ImageSource img = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    
                    img.Freeze(); // 冻结以允许跨线程访问
                    return img;
                }
                finally
                {
                    DestroyIcon(shfi.hIcon);
                }
            }

            return null;
        }

        /// <summary>
        /// 使用物理像素重定位刷新窗口，并强制置于所有普通窗口的最底层（贴合桌面）
        /// </summary>
        public static void ForceShowAndBringToTop(Window window)
        {
            IntPtr hWnd = new WindowInteropHelper(window).Handle;
            if (hWnd == IntPtr.Zero) return;

            var presentationSource = PresentationSource.FromVisual(window);
            IntPtr HWND_BOTTOM = new IntPtr(1);
            if (presentationSource?.CompositionTarget != null)
            {
                var transform = presentationSource.CompositionTarget.TransformToDevice;
                double dpiX = transform.M11;
                double dpiY = transform.M22;

                int x = (int)(window.Left * dpiX);
                int y = (int)(window.Top * dpiY);
                int w = (int)(window.Width * dpiX);
                int h = (int)(window.Height * dpiY);

                SetWindowPos(hWnd, HWND_BOTTOM, x, y, w, h, SWP_SHOWWINDOW);
            }
            else
            {
                // DPI 无法读取时的物理像素兜底
                SetWindowPos(hWnd, HWND_BOTTOM, (int)window.Left, (int)window.Top, (int)window.Width, (int)window.Height, SWP_SHOWWINDOW);
            }
        }

        /// <summary>
        /// 强制刷新 Windows 资源管理器外壳（让移回桌面的文件立刻实时显现，无需右键手动刷新桌面）
        /// </summary>
        public static void RefreshDesktop()
        {
            // SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0x0000
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }

        #region 虚拟桌面多桌面同步跟随支持

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("a5cd92ff-29be-454c-8bc0-4b16ee3afc5b")]
        public interface IVirtualDesktopManager
        {
            int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);
            int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
            int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
        }

        private static IVirtualDesktopManager? _virtualDesktopManager;

        public static void InitVirtualDesktopManager()
        {
            try
            {
                var clsid = new Guid("aa509085-ce26-4f82-0a12-78d4c9473657");
                var type = Type.GetTypeFromCLSID(clsid);
                if (type != null)
                {
                    _virtualDesktopManager = Activator.CreateInstance(type) as IVirtualDesktopManager;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化虚拟桌面管理器失败: {ex.Message}");
            }
        }

        public static Guid GetCurrentVirtualDesktopId()
        {
            try
            {
                // 路径 1 (标准 Windows 10 / 11 虚拟桌面注册表项)
                byte[]? value = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops")
                    ?.GetValue("CurrentVirtualDesktop") as byte[];
                if (value != null && value.Length == 16)
                {
                    return new Guid(value);
                }
            }
            catch { }

            try
            {
                // 路径 2 (多用户 Session ID 隔离路径)
                int sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                byte[]? value = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{sessionId}\VirtualDesktops")
                    ?.GetValue("CurrentVirtualDesktop") as byte[];
                if (value != null && value.Length == 16)
                {
                    return new Guid(value);
                }
            }
            catch { }

            return Guid.Empty;
        }

        public static void SyncWindowToCurrentVirtualDesktop(Window window)
        {
            if (_virtualDesktopManager == null)
            {
                InitVirtualDesktopManager();
            }

            if (_virtualDesktopManager == null) return;

            try
            {
                IntPtr hWnd = new WindowInteropHelper(window).Handle;
                if (hWnd == IntPtr.Zero) return;

                Guid currentDesktopId = GetCurrentVirtualDesktopId();
                if (currentDesktopId != Guid.Empty)
                {
                    _virtualDesktopManager.IsWindowOnCurrentVirtualDesktop(hWnd, out bool onCurrent);
                    if (!onCurrent)
                    {
                        _virtualDesktopManager.MoveWindowToDesktop(hWnd, ref currentDesktopId);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"同步窗口到当前虚拟桌面失败: {ex.Message}");
            }
        }

        #endregion
    }
}
