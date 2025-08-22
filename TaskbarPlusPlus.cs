// Single-file WPF app that creates a transparent, vertical taskbar (appbar) on the left edge.
// It lists icons for currently open top-level windows and lets you activate them by clicking.
// Build: dotnet new wpf -n LeftDockBar (then replace the generated App.xaml/App.xaml.cs/MainWindow* with this single file and update csproj to UseWPF)
// Or compile as a single-file program if your tooling supports it.

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TaskbarPlusPlus
{
    public class App : Application
    {
        [STAThread]
        public static void Main()
        {
            var app = new App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown // do not quit when main window closes
            };

            app.Startup += (_, __) =>
            {
                var win = new DockBarWindow(DockPosition.TOP);
                win.Show();
            };
            app.Run();
        }
    }

    public enum DockPosition
    {
        TOP,
        LEFT
    }

    public class DockBarWindow : Window
    {
        private const int BarWidth = 72; // px
        private readonly StackPanel _stack;
        private readonly DispatcherTimer _refreshTimer;
        private HwndSource _hwndSource;
        private IntPtr _hwnd;
        private bool _appbarRegistered = false;

        // to control the docking position of the task bar
        private DockPosition _dockPosition;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_LWIN = 0x5B;
        private const int KEYEVENTF_KEYUP = 0x0002;

        private void OpenStartMenu()
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
                                         int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint WM_SYSCOMMAND = 0x0112;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        private void ShowSystemMenu(IntPtr targetHwnd)
        {
            IntPtr hMenu = GetSystemMenu(targetHwnd, false);
            if (hMenu == IntPtr.Zero) return;

            GetCursorPos(out POINT pt);

            int cmd = TrackPopupMenu(hMenu, TPM_LEFTALIGN | TPM_RETURNCMD,
                                     pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

            if (cmd != 0)
            {
                SendMessage(targetHwnd, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);
            }
        }

        public DockBarWindow(DockPosition dockPosition = DockPosition.TOP)
        {
            _dockPosition = dockPosition;
            Title = "Taskbar++";
            Width = _dockPosition == DockPosition.TOP ? SystemParameters.WorkArea.Width : BarWidth;
            Height = _dockPosition == DockPosition.TOP ? BarWidth : SystemParameters.WorkArea.Height; // initial, will be set by appbar
            Left = 0;
            Top = 0;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;

            // Root container with docking
            var dock = new DockPanel();

            // Close button
            var closeBtn = new Button
            {
                Content = "X",
                Width = 28,
                Height = 28,
                Margin = new Thickness(4),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "Close Taskbar++"
            };
            closeBtn.Click += (_, __) =>
            {
                this.Close();
                Application.Current.Shutdown();
            };

            DockPanel.SetDock(closeBtn, _dockPosition == DockPosition.TOP ? Dock.Right : Dock.Top);
            dock.Children.Add(closeBtn);

            // toggle switch for dock position
            var switchBtn = new Button
            {
                Content = "TOGGLE",
                Width = 50,
                Height = 28,
                Margin = new Thickness(4),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                ToolTip = "Toggle Dock Position"
            };

            switchBtn.Click += (_, __) =>
            {
                // Flip dock position
                DockPosition newDockPosition = _dockPosition switch
                {
                    DockPosition.LEFT => DockPosition.TOP,
                    DockPosition.TOP => DockPosition.LEFT,
                    _ => DockPosition.TOP
                };

                this.Close();

                // Create new window with new dock position
                var win = new DockBarWindow(newDockPosition);
                win.Show();
            };
            DockPanel.SetDock(switchBtn, _dockPosition == DockPosition.TOP ? Dock.Left : Dock.Bottom);
            dock.Children.Add(switchBtn);

            // UI container
            var border = new Border
            {
                CornerRadius = new CornerRadius(16),
                Margin = new Thickness(6, 8, 6, 8),
                Padding = new Thickness(6),
                Background = new SolidColorBrush(Color.FromArgb(48, 0, 0, 0)), // subtle translucent backdrop for legibility
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 10,
                    ShadowDepth = 0,
                    Opacity = 0.3
                }
            };

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = _dockPosition == DockPosition.TOP ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = _dockPosition == DockPosition.TOP ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
            };

            _stack = new StackPanel
            {
                Orientation = _dockPosition == DockPosition.TOP ? Orientation.Horizontal : Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            scroll.Content = _stack;
            border.Child = scroll;
            dock.Children.Add(border);
            Content = dock;

            Loaded += OnLoaded;
            Closed += OnClosed;

            // Refresh list of windows periodically (and when foreground changes we also get WM events)
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += (_, __) => RefreshWindowList();
            _refreshTimer.Start();
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

            // Remove APPWINDOW (which forces Alt+Tab entry)
            // Add TOOLWINDOW (hidden from Alt+Tab)
            exStyle &= ~WS_EX_APPWINDOW;
            exStyle |= WS_EX_TOOLWINDOW;

            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource.AddHook(WndProc);

            RegisterAsAppBar();
            RefreshWindowList();

            //AppBarInterop.HideSystemTaskbar();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            //try { AppBarInterop.ShowSystemTaskbar(); } catch { }
            try { if (_appbarRegistered) AppBar(AppBarMessage.ABM_REMOVE); } catch { }
            try { _hwndSource?.RemoveHook(WndProc); } catch { }
        }

        #region AppBar logic
        private void RegisterAsAppBar()
        {
            if (_appbarRegistered) return;
            AppBar(AppBarMessage.ABM_NEW);
            _appbarRegistered = true;
            SetAppBarPosition();
        }

        private double GetDpiScaleX()
        {
            var source = PresentationSource.FromVisual(this);
            return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }

        private void SetAppBarPosition()
        {
            // Reserve a strip along the LEFT edge with fixed width
            var screen = SystemParameters.WorkArea; // work area excludes existing taskbar(s)
            var abd = new AppBarInterop.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<AppBarInterop.APPBARDATA>(),
                hWnd = _hwnd,
                uEdge = _dockPosition == DockPosition.TOP ? (uint)ABEdge.ABE_TOP : (uint)ABEdge.ABE_LEFT,
            };

            // Desired rectangle
            abd.rc = new AppBarInterop.RECT
            {
                left = 0,
                top = 0,
                right = _dockPosition == DockPosition.TOP ? (int)SystemParameters.PrimaryScreenWidth : BarWidth,
                bottom = _dockPosition == DockPosition.TOP ? BarWidth : (int)SystemParameters.PrimaryScreenHeight
            };

            // Query position and set
            AppBarInterop.SHAppBarMessage((uint)AppBarMessage.ABM_QUERYPOS, ref abd);

            // Explorer may adjust rc; enforce width and left edge
            int buffer = _dockPosition == DockPosition.TOP ? 10 : 6;
            int pixelWidth = (int)(BarWidth * GetDpiScaleX()) + buffer; // add buffer to prevent icon cutoff

            if (_dockPosition == DockPosition.TOP)
                abd.rc.bottom = abd.rc.top + pixelWidth;
            else
                abd.rc.right = abd.rc.left + pixelWidth;

            AppBarInterop.SHAppBarMessage((uint)AppBarMessage.ABM_SETPOS, ref abd);

            // Apply to our window
            Left = abd.rc.left;
            Top = abd.rc.top;

            if (_dockPosition == DockPosition.TOP)
            {
                Width = (abd.rc.right - abd.rc.left);
                Height = (abd.rc.bottom - abd.rc.top) / GetDpiScaleX();
            }
            else
            {
                Width = (abd.rc.right - abd.rc.left) / GetDpiScaleX();
                Height = (abd.rc.bottom - abd.rc.top);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM.WINDOWPOSCHANGING:
                case WM.DISPLAYCHANGE:
                    SetAppBarPosition();
                    break;
                case WM.DWMCOMPOSITIONCHANGED:
                    // no-op, but could adjust blur/transparency if desired
                    break;
                case WM.APPBAR_CALLBACK:
                    if (wParam.ToInt32() == (int)ABNotify.ABN_POSCHANGED)
                        SetAppBarPosition();
                    break;
            }
            return IntPtr.Zero;
        }

        private void AppBar(AppBarMessage msg)
        {
            var abd = new AppBarInterop.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<AppBarInterop.APPBARDATA>(),
                hWnd = _hwnd,
                uCallbackMessage = WM.APPBAR_CALLBACK
            };
            AppBarInterop.SHAppBarMessage((uint)msg, ref abd);
        }
        #endregion

        #region Populate icons
        private void RefreshWindowList()
        {
            var windows = WindowEnumerator.GetTopLevelWindows();

            _stack.Children.Clear();

            foreach (var w in windows)
            {
                var btn = new Button
                {
                    Margin = new Thickness(4),
                    Padding = new Thickness(6),
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    ToolTip = w.Title
                };

                var img = new Image
                {
                    Width = 32,
                    Height = 32,
                    Stretch = Stretch.Uniform,
                    Source = w.Icon ?? PlaceholderIcon()
                };
                btn.Content = img;

                btn.Click += (_, __) =>
                {
                    WindowEnumerator.ActivateWindow(w.HWnd);
                };

                btn.MouseRightButtonUp += (_, __) =>
                {
                    ShowSystemMenu(w.HWnd);
                };

                _stack.Children.Add(btn);
            }

            string startIconFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources/win_start_menu_icon.png");
            var startButton = new Button
            {
                Width = 40,
                Height = 40,
                Margin = new Thickness(4),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Content = new Image
                {
                    Source = new BitmapImage(new Uri(startIconFilePath, UriKind.Absolute)),
                    Stretch = Stretch.Uniform
                }
            };
            startButton.Click += (_, __) => OpenStartMenu();

            _stack.Children.Insert(0, startButton);
        }

        private ImageSource PlaceholderIcon()
        {
            // Simple vector-ish placeholder (a circle) drawn via DrawingImage
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing
            {
                Brush = Brushes.Gray,
                Geometry = new EllipseGeometry(new System.Windows.Point(16, 16), 12, 12)
            });
            return new DrawingImage(group);
        }
        #endregion
    }

    #region Win enumeration and interop helpers
    public static class WindowEnumerator
    {
        public record TopWindow(IntPtr HWnd, string Title, ImageSource Icon);

        public static IEnumerable<TopWindow> GetTopLevelWindows()
        {
            var list = new List<TopWindow>();
            EnumWindows((hwnd, l) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (GetWindow(hwnd, (int)GW.GW_OWNER) != IntPtr.Zero) return true; // skip owned

                // Skip toolwindows
                var ex = GetWindowLongPtr(hwnd, (int)GWL.GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;

                // Skip cloaked (UWP minimized/hidden etc.)
                if (IsWindowCloaked(hwnd)) return true;

                int len = GetWindowTextLength(hwnd);
                if (len == 0) return true; // no title
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();

                var icon = GetWindowIcon(hwnd) ?? GetProcessIcon(hwnd);

                list.Add(new TopWindow(hwnd, title, icon));
                return true;
            }, IntPtr.Zero);

            // De-duplicate by process and z-order stable sort
            return list
                .GroupBy(w => w.Title + ":" + w.HWnd)
                .Select(g => g.First())
                .ToList();
        }

        public static void ActivateWindow(IntPtr hwnd)
        {
            // Restore if minimized then set foreground
            ShowWindow(hwnd, (int)SW.RESTORE);
            SetForegroundWindow(hwnd);
        }

        private static bool IsWindowCloaked(IntPtr hwnd)
        {
            int cloaked = 0;
            int DWMWA_CLOAKED = 14;
            int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, Marshal.SizeOf<int>());
            return hr == 0 && cloaked != 0;
        }

        private static ImageSource GetWindowIcon(IntPtr hwnd)
        {
            // Try WM_GETICON (large, small)
            IntPtr hIcon = SendMessage(hwnd, (int)WM_GETICON, new IntPtr(2), IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hwnd, (int)WM_GETICON, new IntPtr(1), IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hwnd, (int)WM_GETICON, IntPtr.Zero, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
            {
                // Try class icon
                hIcon = GetClassLongPtr(hwnd, (int)GCLP.HICON);
                if (hIcon == IntPtr.Zero)
                    hIcon = GetClassLongPtr(hwnd, (int)GCLP.HICONSM);
            }
            if (hIcon == IntPtr.Zero) return null;
            return Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }

        private static ImageSource GetProcessIcon(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                var hProcess = OpenProcess(ProcessAccessFlags.QueryLimitedInformation | ProcessAccessFlags.QueryInformation, false, pid);
                if (hProcess == IntPtr.Zero) return null;
                var sb = new StringBuilder(260);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                {
                    var path = sb.ToString();
                    var shinfo = new SHFILEINFO();
                    IntPtr hImg = SHGetFileInfo(path, 0, out shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(), (uint)(SHGFI.Icon | SHGFI.LargeIcon));
                    if (hImg != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                    {
                        return Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    }
                }
                return null;
            }
            catch { return null; }
        }

        #region PInvoke
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_GETICON = 0x007F;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)] private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);
        private enum GCLP { HICON = -14, HICONSM = -34 }

        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [Flags]
        private enum ProcessAccessFlags : uint
        {
            QueryInformation = 0x0400,
            QueryLimitedInformation = 0x1000
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(ProcessAccessFlags access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

        [StructLayout(LayoutKind.Sequential)] private struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; public IntPtr szDisplayName; public IntPtr szTypeName; }
        [Flags] private enum SHGFI : uint { Icon = 0x000000100, LargeIcon = 0x000000000 }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        private enum GW { GW_OWNER = 4 }
        private enum GWL { GWL_EXSTYLE = -20 }
        private enum SW { RESTORE = 9 }
        #endregion
    }
    #endregion

    #region AppBar interop
    internal static class WM
    {
        public const int APPBAR_CALLBACK = 0xA123; // arbitrary app-defined message id
        public const int WINDOWPOSCHANGING = 0x0046;
        public const int DISPLAYCHANGE = 0x007E;
        public const int DWMCOMPOSITIONCHANGED = 0x031E;
    }

    internal enum AppBarMessage : uint
    {
        ABM_NEW = 0x00000000,
        ABM_REMOVE = 0x00000001,
        ABM_QUERYPOS = 0x00000002,
        ABM_SETPOS = 0x00000003,
        ABM_GETSTATE = 0x00000004,
        ABM_GETTASKBARPOS = 0x00000005,
        ABM_ACTIVATE = 0x00000006,
        ABM_GETAUTOHIDEBAR = 0x00000007,
        ABM_SETAUTOHIDEBAR = 0x00000008,
        ABM_WINDOWPOSCHANGED = 0x00000009,
        ABM_SETSTATE = 0x0000000A
    }

    internal enum ABEdge : uint { ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3 }

    internal enum ABNotify : int { ABN_STATECHANGE = 0, ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2, ABN_WINDOWARRANGE = 3 }

    internal static class AppBarInterop
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public int lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int left, top, right, bottom;
        }

        [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        internal const int SW_HIDE = 0;
        internal const int SW_SHOW = 5;
        
        internal static void HideSystemTaskbar()
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                ShowWindow(taskbar, SW_HIDE);
            }

            // also hide the Start button (sometimes separate window)
            IntPtr startBtn = FindWindow("Button", null);
            if (startBtn != IntPtr.Zero)
            {
                ShowWindow(startBtn, SW_HIDE);
            }
        }

        internal static void ShowSystemTaskbar()
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                ShowWindow(taskbar, SW_SHOW);
            }

            IntPtr startBtn = FindWindow("Button", null);
            if (startBtn != IntPtr.Zero)
            {
                ShowWindow(startBtn, SW_SHOW);
            }
        }
    }
    #endregion
}
