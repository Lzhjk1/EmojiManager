using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace EmojiManager
{
    public partial class MainWindow
    {
        private const int HotkeyId = 9000;

        private HwndSource? _source;
        private Settings _settings = null!;
        private bool _isVisible;
        private bool _isPinned;
        private FileSystemWatcher? _fileWatcher;
        private readonly object _reloadLock = new();
        private CancellationTokenSource? _reloadCts;
        private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
        private TaskbarIcon? _taskbarIcon;
        private System.Windows.Threading.DispatcherTimer? _foregroundWindowTracker;
        private static readonly TimeSpan ReloadDebounceInterval = TimeSpan.FromMilliseconds(300);
        
        // 缓存 JsonSerializerOptions 实例
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [LibraryImport("user32.dll")]
        private static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VkControl = 0x11;
        private const byte VkV = 0x56;
        private const uint KeyeventfKeyup = 0x0002;

        private IntPtr _lastActiveWindow = IntPtr.Zero;
        private bool _shouldPasteAfterDeactivate;
        private IntPtr _previousForegroundWindow = IntPtr.Zero;

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            InitializeWindow();
            InitializeFileWatcher();
            InitializeTaskbarIcon();
            StartForegroundWindowTracking();
        }

        private void LoadSettings()
        {
            try
            {
                _settings = Settings.Load();
            }
            catch (Exception ex)
            {
                // 如果加载设置失败，使用默认设置
                _settings = new Settings();
                MessageBox.Show($"加载设置时发生错误，将使用默认设置: {ex.Message}", "警告",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void InitializeWindow()
        {
            // 从设置中恢复窗口属性
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
            WindowState = _settings.WindowState;

            // 恢复窗口位置
            if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
            {
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;

                // 确保窗口在屏幕范围内
                EnsureWindowInBounds();
            }
            else
            {
                // 默认位置在屏幕右下角
                var workingArea = SystemParameters.WorkArea;
                Left = workingArea.Right - Width - 20;
                Top = workingArea.Bottom - Height - 20;
            }

            // 恢复钉住状态
            _isPinned = _settings.IsPinned;
            Topmost = _isPinned;

            // 窗口初始显示，让用户知道程序已启动
            _isVisible = true;
        }

        private void EnsureWindowInBounds()
        {
            var workingArea = SystemParameters.WorkArea;

            // 确保窗口不超出屏幕边界
            if (Left < workingArea.Left) Left = workingArea.Left;
            if (Top < workingArea.Top) Top = workingArea.Top;
            if (Left + Width > workingArea.Right) Left = workingArea.Right - Width;
            if (Top + Height > workingArea.Bottom) Top = workingArea.Bottom - Height;
        }

        private void InitializeFileWatcher()
        {
            // 检查表情包路径是否存在，如果不存在则不启用文件监听
            if (!Directory.Exists(_settings.EmojiBasePath))
            {
                return; // 路径不存在时跳过文件监听器初始化
            }

            try
            {
                _fileWatcher = new FileSystemWatcher(_settings.EmojiBasePath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };

                _fileWatcher.Created += OnFileSystemChanged;
                _fileWatcher.Deleted += OnFileSystemChanged;
                _fileWatcher.Renamed += OnFileSystemChanged;
                _fileWatcher.EnableRaisingEvents = true;
            }
            catch
            {
                // 如果初始化失败，忽略错误继续运行
                _fileWatcher?.Dispose();
                _fileWatcher = null;
            }
        }

        private void InitializeTaskbarIcon()
        {
            try
            {
                _taskbarIcon = new TaskbarIcon();

                // 设置托盘图标路径
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    var bitmapImage = new System.Windows.Media.Imaging.BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.UriSource = new Uri(iconPath, UriKind.Absolute);
                    bitmapImage.EndInit();
                    _taskbarIcon.IconSource = bitmapImage;
                }
                else
                {
                    // 如果图标文件不存在，创建一个简单的默认图标
                    _taskbarIcon.IconSource = CreateDefaultIcon;
                }

                _taskbarIcon.ToolTipText = "表情管理器";

                // 左键单击事件
                _taskbarIcon.TrayLeftMouseUp += (_, _) =>
                {
                    if (_isVisible)
                    {
                        HideWindow();
                    }
                    else
                    {
                        // 使用之前记录的前台窗口，而不是当前的（可能已经不是QQNT了）
                        _lastActiveWindow = _previousForegroundWindow;
                        ShowWindowFromTray();
                    }
                };

                // 右键菜单
                var contextMenu = new System.Windows.Controls.ContextMenu();

                var exitMenuItem = new System.Windows.Controls.MenuItem
                {
                    Header = "退出程序"
                };
                exitMenuItem.Click += (_, _) =>
                {
                    ExitApplication();
                };
                contextMenu.Items.Add(exitMenuItem);

                _taskbarIcon.ContextMenu = contextMenu;
            }
            catch (Exception ex)
            {
                // 如果托盘图标初始化失败，记录错误但继续运行
                MessageBox.Show($"托盘图标初始化失败: {ex.Message}", "警告",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static System.Windows.Media.ImageSource CreateDefaultIcon
        {
            get
            {
                // 创建一个简单的默认图标（16x16像素的纯色图标）
                var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(16, 16, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null);

                // 填充为蓝色
                var color = System.Windows.Media.Colors.DodgerBlue;
                var pixels = new uint[16 * 16];
                var colorValue = (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);

                for (var i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = colorValue;
                }

                bitmap.WritePixels(new Int32Rect(0, 0, 16, 16), pixels, 16 * 4, 0);
                return bitmap;
            }
        }

        private void StartForegroundWindowTracking()
        {
            // 启动一个定时器，定期记录前台窗口（仅在窗口隐藏时）
            _foregroundWindowTracker = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500) // 每500ms检查一次
            };

            _foregroundWindowTracker.Tick += (_, _) =>
            {
                // 只有在窗口隐藏时才更新前台窗口记录
                if (_isVisible)
                    return;
                var currentForeground = GetForegroundWindow();
                // 避免记录自己的窗口句柄
                if (currentForeground != new WindowInteropHelper(this).Handle)
                {
                    _previousForegroundWindow = currentForeground;
                }
            };

            _foregroundWindowTracker.Start();
        }

        private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
        {
            // 检查是否应该处理此文件变化
            var shouldProcess = false;
            var extension = Path.GetExtension(e.FullPath).ToLower();

            // 删除操作总是处理
            if (e.ChangeType == WatcherChangeTypes.Deleted)
            {
                shouldProcess = true;
            }
            else
            {
                // 检查是否为已知的图片格式
                var supportedExtensions = ImageFormatDetector.GetSupportedExtensions();
                var extensionsWithDot = supportedExtensions.Select(ext => "." + ext);

                if (extensionsWithDot.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    shouldProcess = true;
                }
                // 或者是可疑的文件（可能是QQNT错误命名的图片）
                else if (extension == ".null" || string.IsNullOrEmpty(extension) ||
                         !IsCommonNonImageExtension(extension))
                {
                    shouldProcess = true;
                }
            }

            if (shouldProcess)
            {
                QueueEmojiReload();
            }
        }

        private void QueueEmojiReload()
        {
            CancellationTokenSource cts;
            lock (_reloadLock)
            {
                _reloadCts?.Cancel();
                _reloadCts?.Dispose();
                _reloadCts = new CancellationTokenSource();
                cts = _reloadCts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ReloadDebounceInterval, cts.Token);
                    var loadTask = await Dispatcher.InvokeAsync(() => LoadEmojiData(cts.Token));
                    await loadTask;
                }
                catch (OperationCanceledException)
                {
                    // 忽略被取消的刷新请求
                }
                catch (Exception ex)
                {
                    _ = Dispatcher.InvokeAsync(() => ShowToast($"刷新失败: {ex.Message}", ToastType.Error));
                }
            });
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 注册热键
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!;
            _source.AddHook(HwndHook);
            RegisterHotkey();

            // 初始化WebView2
            await InitializeWebView();
        }

        private void RegisterHotkey()
        {
            if (_source?.Handle != null && _source.Handle != IntPtr.Zero)
            {
                // 先注销之前的热键
                UnregisterHotKey(_source.Handle, HotkeyId);
                // 注册新的热键
                RegisterHotKey(_source.Handle, HotkeyId, _settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
            }
        }

        /// <summary>
        /// 临时注销快捷键（用于测试）
        /// </summary>
        public bool TemporarilyUnregisterHotkey()
        {
            if (_source?.Handle != null && _source.Handle != IntPtr.Zero)
            {
                UnregisterHotKey(_source.Handle, HotkeyId);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 恢复快捷键注册
        /// </summary>
        public void RestoreHotkeyRegistration()
        {
            RegisterHotkey();
        }

        /// <summary>
        /// 刷新表情数据（供设置窗口调用）
        /// </summary>
        public Task RefreshEmojiData()
        {
            QueueEmojiReload();
            return Task.CompletedTask;
        }

        private async Task InitializeWebView()
        {
            await WebView.EnsureCoreWebView2Async();

            // 设置WebView2选项
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            WebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
            WebView.CoreWebView2.Settings.IsScriptEnabled = true;

            // 设置虚拟主机映射以访问本地文件
            await SetupVirtualHostMapping();

            // 注册JavaScript交互
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // 加载HTML内容
            var htmlContent = await GetHtmlContent();
            WebView.NavigateToString(htmlContent);

            // 等待页面加载完成后加载表情数据
            WebView.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    QueueEmojiReload();
                    _ = UpdatePinnedState();
                }
            };
        }

        /// <summary>
        /// 设置WebView2的虚拟主机映射
        /// </summary>
        private async Task SetupVirtualHostMapping()
        {
            if (WebView?.CoreWebView2 == null)
                return;

            try
            {
                // 先尝试清除现有的虚拟主机映射
                try
                {
                    WebView.CoreWebView2.ClearVirtualHostNameToFolderMapping("local.images");
                }
                catch
                {
                    // 忽略清除失败的错误（可能映射不存在）
                }

                // 确保表情包路径存在
                var emojiPath = _settings.EmojiBasePath;
                if (string.IsNullOrEmpty(emojiPath))
                {
                    // 使用默认路径
                    emojiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "表情包");
                    _settings.EmojiBasePath = emojiPath;
                }

                // 如果路径不存在，尝试创建
                if (!Directory.Exists(emojiPath))
                {
                    try
                    {
                        Directory.CreateDirectory(emojiPath);
                    }
                    catch
                    {
                        // 如果无法创建，显示提示并使用临时目录
                        await ShowToast("无法访问表情包目录，请检查路径设置", ToastType.Error);
                        emojiPath = Path.GetTempPath();
                    }
                }

                // 规范化路径（确保是绝对路径）
                emojiPath = Path.GetFullPath(emojiPath);

                // 设置新的虚拟主机映射
                WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "local.images",
                    emojiPath,
                    CoreWebView2HostResourceAccessKind.Allow);

                // 等待映射设置生效
                await Task.Delay(100);

                Console.WriteLine($"Virtual host mapping set: local.images -> {emojiPath}");
            }
            catch (Exception ex)
            {
                await ShowToast($"设置文件访问权限失败: {ex.Message}", ToastType.Error);
                Console.WriteLine($"SetupVirtualHostMapping failed: {ex}");
            }
        }

        private static async Task<string> GetHtmlContent()
        {
            // 尝试从同目录下加载HTML文件
            var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EmojiManager.html");
            if (File.Exists(htmlPath))
            {
                return await File.ReadAllTextAsync(htmlPath);
            }

            // 如果文件不存在，返回内嵌的HTML
            return GetEmbeddedHtml();
        }

        private async Task LoadEmojiData(CancellationToken cancellationToken = default)
        {
            if (WebView?.CoreWebView2 == null)
            {
                return;
            }

            await _loadSemaphore.WaitAsync(cancellationToken);
            try
            {
                // 清理无效的最近表情
                _settings.CleanupRecentEmojis();

                var basePath = _settings.EmojiBasePath;
                var recentEmojis = _settings.RecentEmojis.ToList();
                var recentLimit = _settings.RecentEmojisLimit;
                var enableFilenameSearch = _settings.EnableFilenameSearch;
                var baseThumbnailSize = _settings.BaseThumbnailSize;
                var enableCtrlScrollResize = _settings.EnableCtrlScrollResize;
                var recentEmojiScale = _settings.RecentEmojiScale;

                var dataObject = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var emojiData = ScanEmojiDirectory(basePath, cancellationToken);

                    // 按自定义顺序重排根目录下的文件夹（仅根目录，子文件夹保持文件系统顺序）
                    ApplyFolderOrder(emojiData, LoadFolderOrder(basePath));

                    // 构建最近表情文件夹
                    var recentFolder = new EmojiFolder
                    {
                        Name = "最近使用",
                        Path = "",
                        Images = [.. recentEmojis],
                        Children = []
                    };

                    // 将最近表情插入到文件夹列表的最前面（只有当有最近表情时）
                    var allFolders = new List<EmojiFolder>();
                    if (recentFolder.Images.Count > 0)
                    {
                        allFolders.Add(recentFolder);
                    }
                    allFolders.AddRange(emojiData);

                    // 加载所有文件夹的缩放配置
                    var folderScales = LoadAllFolderScales(basePath);
                    
                    // 添加最近使用表情的缩放配置
                    if (recentEmojiScale != 1.0)
                    {
                        folderScales[""] = recentEmojiScale;
                    }

                    // 加载所有子文件夹的图片备注，聚合为以绝对路径为 Key 的字典
                    var absoluteRemarks = LoadAllFolderRemarks(basePath);

                    return new
                    {
                        folders = allFolders,
                        basePath,
                        recentLimit,
                        enableFilenameSearch,
                        baseThumbnailSize,
                        enableCtrlScrollResize,
                        folderScales,
                        imageRemarks = absoluteRemarks
                    };
                }, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var json = JsonSerializer.Serialize(dataObject, JsonOptions);
                await WebView.CoreWebView2.ExecuteScriptAsync($"loadEmojiData({json})");
            }
            catch (OperationCanceledException)
            {
                // 忽略被取消的刷新
            }
            catch (Exception ex)
            {
                await ShowToast($"刷新失败: {ex.Message}", ToastType.Error);
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        /// <summary>
        /// 按自定义顺序重排文件夹列表。
        /// 顺序列表中存在的文件夹按其索引排列；不在列表中的（新增文件夹）追加到末尾，保持原顺序。
        /// 顺序列表为空时不做任何调整。
        /// </summary>
        private static void ApplyFolderOrder(List<EmojiFolder> folders, List<string> order)
        {
            if (order.Count == 0 || folders.Count <= 1)
                return;

            // 建立 名称 -> 顺序索引 的映射（忽略大小写）
            var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < order.Count; i++)
            {
                orderIndex[order[i]] = i;
            }

            // 稳定排序：在 order 中的按索引升序，不在的按 int.MaxValue 保持原相对顺序
            var decorated = folders
                .Select((f, idx) => (folder: f, orderPos: orderIndex.TryGetValue(f.Name, out var p) ? p : int.MaxValue, origIdx: idx))
                .ToList();

            decorated.Sort((a, b) =>
            {
                var c = a.orderPos.CompareTo(b.orderPos);
                return c != 0 ? c : a.origIdx.CompareTo(b.origIdx);
            });

            for (var i = 0; i < folders.Count; i++)
            {
                folders[i] = decorated[i].folder;
            }
        }

        private List<EmojiFolder> ScanEmojiDirectory(string path, CancellationToken cancellationToken)
        {
            var result = new List<EmojiFolder>();

            if (!Directory.Exists(path))
                return result;

            try
            {
                var directories = Directory.GetDirectories(path);
                foreach (var dir in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var folder = new EmojiFolder
                    {
                        Name = Path.GetFileName(dir),
                        Path = dir,
                        Images = GetImages(dir, cancellationToken),
                        Children = ScanEmojiDirectory(dir, cancellationToken)
                    };

                    if (folder.Images.Count > 0 || folder.Children.Count > 0)
                    {
                        result.Add(folder);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 忽略无权限访问的文件夹
            }

            return result;
        }

        /// <summary>
        /// 按自定义顺序重排图片路径列表（就地修改）。
        /// 顺序列表存的是文件名，按其索引匹配；不在列表中的（新增图片）排到最前面，保持原顺序。
        /// 顺序列表为空时不做任何调整。
        /// </summary>
        private static void ApplyImageOrder(List<string> imagePaths, List<string> order)
        {
            if (order.Count == 0 || imagePaths.Count <= 1)
                return;

            var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < order.Count; i++)
            {
                orderIndex[order[i]] = i;
            }

            var decorated = imagePaths
                .Select((p, idx) => (path: p, orderPos: orderIndex.TryGetValue(Path.GetFileName(p), out var v) ? v : -1, origIdx: idx))
                .ToList();

            decorated.Sort((a, b) =>
            {
                var c = a.orderPos.CompareTo(b.orderPos);
                return c != 0 ? c : a.origIdx.CompareTo(b.origIdx);
            });

            for (var i = 0; i < imagePaths.Count; i++)
            {
                imagePaths[i] = decorated[i].path;
            }
        }

        private List<string> GetImages(string path, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var validImages = new List<string>();
                var supportedExtensions = ImageFormatDetector.GetSupportedExtensions();

                // 首先按扩展名筛选已知的图片文件
                var extensionsWithDot = supportedExtensions.Select(ext => "." + ext).ToArray();
                var filesByExtension = Directory.GetFiles(path)
                    .Where(f => extensionsWithDot.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .ToList();

                validImages.AddRange(filesByExtension);

                // 然后检查那些可能被QQNT错误命名的文件（如.null, 无扩展名等）
                var suspiciousFiles = Directory.GetFiles(path)
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f).ToLower();
                        return ext == ".null" || string.IsNullOrEmpty(ext) ||
                               (!extensionsWithDot.Contains(ext, StringComparer.OrdinalIgnoreCase) &&
                                !IsCommonNonImageExtension(ext));
                    })
                    .ToList();

                // 对可疑文件进行格式检测
                foreach (var file in suspiciousFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (ImageFormatDetector.DetectImageFormatFromFile(file) != null)
                        {
                            validImages.Add(file);
                        }
                    }
                    catch
                    {
                        // 忽略无法读取的文件
                    }
                }

                // 根据设置排序图片
                if (_settings.SortImagesByTime)
                {
                    // 按创建时间排序（从最新到最老）
                    validImages = [.. validImages.OrderByDescending(file =>
                    {
                        try
                        {
                            return File.GetCreationTime(file);
                        }
                        catch
                        {
                            return DateTime.MinValue; // 无法获取时间的文件排在最后
                        }
                    })];
                }
                else
                {
                    // 按文件名排序（默认行为）
                    validImages.Sort(StringComparer.OrdinalIgnoreCase);
                }

                // 若该文件夹有自定义图片顺序，覆盖默认排序
                ApplyImageOrder(validImages, LoadImageOrder(path));

                return validImages;
            }
            catch
            {
                return [];
            }
        }

        /// <summary>
        /// 检查是否为常见的非图片扩展名
        /// </summary>
        private static bool IsCommonNonImageExtension(string extension)
        {
            var nonImageExtensions = new[]
            {
                ".txt", ".doc", ".docx", ".pdf", ".zip", ".rar", ".exe", ".dll",
                ".mp3", ".mp4", ".avi", ".mov", ".mkv", ".wav", ".flac",
                ".json", ".xml", ".html", ".css", ".js", ".cs", ".cpp", ".h"
            };

            return nonImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 修正指定目录下所有图片文件的扩展名
        /// </summary>
        /// <param name="rootPath">要处理的根目录路径</param>
        /// <param name="progress">进度回调</param>
        /// <returns>修正结果统计</returns>
        public static async Task<(int corrected, int skipped, int errors)> CorrectImageExtensions(string rootPath, IProgress<ImageCorrectionProgress>? progress = null)
        {
            var correctedCount = 0;
            var skippedCount = 0;
            var errorCount = 0;
            var processedCount = 0;
            var totalFiles = 0;

            try
            {
                await Task.Run(() =>
                {
                    totalFiles = CountFilesSafe(rootPath);
                    progress?.Report(new ImageCorrectionProgress(0, totalFiles));
                    ProcessDirectory(rootPath, ref correctedCount, ref skippedCount, ref errorCount, ref processedCount, totalFiles, progress);
                    progress?.Report(new ImageCorrectionProgress(processedCount, totalFiles));
                });
            }
            catch
            {
                errorCount++;
            }

            return (correctedCount, skippedCount, errorCount);

            static int CountFilesSafe(string directory)
            {
                try
                {
                    return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
                }
                catch
                {
                    return 0;
                }
            }

            static void ProcessDirectory(
                string directory,
                ref int corrected,
                ref int skipped,
                ref int errors,
                ref int processed,
                int total,
                IProgress<ImageCorrectionProgress>? progress)
            {
                const int ReportBatchSize = 25;
                try
                {
                    // 处理当前目录的文件
                    var files = Directory.GetFiles(directory);
                    foreach (var file in files)
                    {
                        try
                        {
                            var actualFormat = ImageFormatDetector.DetectImageFormatFromFile(file);

                            if (actualFormat != null)
                            {
                                var currentExt = Path.GetExtension(file).TrimStart('.').ToLower();
                                if (currentExt != actualFormat && currentExt != "null") // 不处理.null文件，让拖拽功能处理
                                {
                                    var fileDirectory = Path.GetDirectoryName(file)!;
                                    var nameWithoutExt = Path.GetFileNameWithoutExtension(file);
                                    var newFileName = $"{nameWithoutExt}.{actualFormat}";
                                    var newFilePath = Path.Combine(fileDirectory, newFileName);

                                    if (File.Exists(newFilePath))
                                    {
                                        // 如果目标文件已存在，删除原文件；同步清理原文件的孤儿备注
                                        File.Delete(file);
                                        SaveImageRemark(file, string.Empty);
                                        skipped++;
                                    }
                                    else
                                    {
                                        // 重命名文件；同步迁移备注的键名
                                        File.Move(file, newFilePath);
                                        RenameImageRemark(file, newFilePath);
                                        corrected++;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            errors++;
                        }
                        finally
                        {
                            processed++;
                            if (progress != null && (processed % ReportBatchSize == 0 || (total > 0 && processed == total)))
                            {
                                progress.Report(new ImageCorrectionProgress(processed, total));
                            }
                        }
                    }

                    // 递归处理子目录
                    var subdirectories = Directory.GetDirectories(directory);
                    foreach (var subdirectory in subdirectories)
                    {
                        ProcessDirectory(subdirectory, ref corrected, ref skipped, ref errors, ref processed, total, progress);
                    }
                }
                catch
                {
                    errors++;
                }
            }
        }

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var message = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(message)) return;

            try
            {
                // 先尝试解析为缩放相关消息
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("type", out var typeElement))
                {
                    var type = typeElement.GetString();
                    
                    // 处理缩放相关消息
                    switch (type)
                    {
                        case "saveFolderScale":
                            if (root.TryGetProperty("folderPath", out var folderPathElement) &&
                                root.TryGetProperty("scale", out var scaleElement))
                            {
                                var folderPath = folderPathElement.GetString();
                                var scale = scaleElement.GetDouble();
                                if (!string.IsNullOrEmpty(folderPath))
                                {
                                    // 如果不是绝对路径，将其转换为绝对路径
                                    var fullPath = Path.IsPathRooted(folderPath) ? 
                                        folderPath : 
                                        Path.Combine(_settings.EmojiBasePath, folderPath);
                                    SaveFolderScale(fullPath, scale);
                                }
                            }
                            return;
                            
                        case "deleteFolderScale":
                            if (root.TryGetProperty("folderPath", out var delFolderPathElement))
                            {
                                var folderPath = delFolderPathElement.GetString();
                                if (!string.IsNullOrEmpty(folderPath))
                                {
                                    // 如果不是绝对路径，将其转换为绝对路径
                                    var fullPath = Path.IsPathRooted(folderPath) ? 
                                        folderPath : 
                                        Path.Combine(_settings.EmojiBasePath, folderPath);
                                    DeleteFolderScale(fullPath);
                                }
                            }
                            return;
                            
                        case "saveRecentEmojiScale":
                            if (root.TryGetProperty("scale", out var recentScaleElement))
                            {
                                var scale = recentScaleElement.GetDouble();
                                _settings.RecentEmojiScale = scale;
                                _settings.Save();
                            }
                            return;
                            
                        case "resetRecentEmojiScale":
                            _settings.RecentEmojiScale = 1.0;
                            _settings.Save();
                            return;

                        case "reorderFolders":
                            if (root.TryGetProperty("folders", out var foldersElement))
                            {
                                var folderOrder = foldersElement.EnumerateArray()
                                    .Select(f => f.GetString() ?? string.Empty)
                                    .Where(s => !string.IsNullOrEmpty(s))
                                    .ToList();
                                SaveFolderOrder(_settings.EmojiBasePath, folderOrder);
                            }
                            return;

                        case "reorderImages":
                            if (root.TryGetProperty("folderPath", out var imgOrderPathElement) &&
                                root.TryGetProperty("images", out var imagesElement))
                            {
                                var folderPath = imgOrderPathElement.GetString();
                                if (!string.IsNullOrEmpty(folderPath))
                                {
                                    // 前端传绝对路径，转文件名后保存
                                    var imageOrder = imagesElement.EnumerateArray()
                                        .Select(p => Path.GetFileName(p.GetString() ?? string.Empty))
                                        .Where(s => !string.IsNullOrEmpty(s))
                                        .ToList();
                                    SaveImageOrder(folderPath, imageOrder);
                                }
                            }
                            return;

                        case "setRemark":
                            if (root.TryGetProperty("imagePath", out var imgPathElement) &&
                                root.TryGetProperty("remark", out var remarkElement))
                            {
                                var imgPath = imgPathElement.GetString();
                                var remark = remarkElement.GetString();
                                if (!string.IsNullOrEmpty(imgPath))
                                {
                                    SaveImageRemark(imgPath, remark ?? string.Empty);
                                }
                            }
                            return;
                    }
                }

                // 如果不是缩放消息，尝试作为 WebMessage 处理
                WebMessage? data = null;
                try
                {
                    data = JsonSerializer.Deserialize<WebMessage>(message, JsonOptions);
                }
                catch (JsonException)
                {
                    // 如果无法解析为 WebMessage，说明是未知消息格式，直接返回
                    Console.WriteLine($"Unknown message format: {message?[..Math.Min(100, message?.Length ?? 0)]}");
                    return;
                }

                switch (data?.Type)
                {
                    case "copyImage":
                        await CopyImageToClipboard(data.Path);
                        
                        // 记录到最近使用表情
                        _settings.AddRecentEmoji(data.Path);

                        // 只更新最近表情分组，避免全量刷新
                        await UpdateRecentEmojisOnly();

                        _shouldPasteAfterDeactivate = true; // 设置粘贴标志
                        if (!_isPinned)
                        {
                            HideWindow();
                        }
                        else
                        {
                            // 即使钉住也要还原焦点并粘贴
                            RestoreFocusAndPaste();
                            _shouldPasteAfterDeactivate = false; // 立即重置标志
                        }
                        break;

                    case "hideWindow":
                        HideWindow();
                        break;

                    case "togglePin":
                        TogglePin();
                        break;

                    case "dropFiles":
                        await HandleDropFiles(data.Files, data.TargetPath);
                        break;

                    case "prepareFolderContextMenu":
                        await ShowFolderContextMenu(data.TargetPath, data.X, data.Y, data.RequestId);
                        break;

                    case "pasteClipboardImage":
                        await PasteClipboardImageToFolder(data.TargetPath);
                        break;

                    case "openLocation":
                        OpenFileLocation(data.Path);
                        break;

                    case "deleteImage":
                        await DeleteImageFile(data.Path);
                        break;

                    case "openSettings":
                        OpenSettingsWindow();
                        break;
                }
            }
            catch (Exception ex)
            {
                // 只有非预期的错误才显示给用户
                await ShowToast($"错误: {ex.Message}", ToastType.Error);
            }
        }

        private async Task ShowToast(string message, ToastType type)
        {
            var toastTypeStr = type switch
            {
                ToastType.Success => "success",
                ToastType.Error => "error",
                _ => "info"
            };

            await WebView.CoreWebView2.ExecuteScriptAsync(
                $"handleMessage({{type: 'showToast', text: '{message.Replace("'", "\\'")}', toastType: '{toastTypeStr}'}})");
        }

        private async Task UpdateRecentEmojisOnly()
        {
            if (WebView?.CoreWebView2 == null)
            {
                return;
            }

            var recentEmojis = _settings.RecentEmojis.ToList();
            var json = JsonSerializer.Serialize(recentEmojis, JsonOptions);
            await WebView.CoreWebView2.ExecuteScriptAsync($"updateRecentEmojis({json})");
        }

        [GeneratedRegex(@"^[a-fA-F0-9]{32}$")]
        private static partial Regex Md5FileNameRegex();

        private static bool IsMd5FileName(string fileName)
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            return Md5FileNameRegex().IsMatch(nameWithoutExt);
        }

        private async Task HandleDropFiles(List<FileData>? files, string targetPath)
        {
            if (files == null || files.Count == 0)
                return;

            var counters = new SaveCounters();

            try
            {
                foreach (var fileData in files)
                {
                    if (string.IsNullOrEmpty(fileData.Name) || fileData.Content == null)
                        continue;

                    // 将Base64内容解码为字节数组
                    var bytes = Convert.FromBase64String(fileData.Content);

                    // 复用统一的图片保存逻辑，保持拖拽与剪贴板导入行为一致
                    counters.Apply(await SaveImageToFolder(targetPath, fileData.Name, bytes));
                }

                await ShowToast(counters.BuildToastMessage(), counters.HasSuccess ? ToastType.Success : ToastType.Info);

                if (counters.HasSuccess)
                {
                    QueueEmojiReload();
                }
            }
            catch (Exception ex)
            {
                await ShowToast($"添加失败: {ex.Message}", ToastType.Error);
            }
        }

        private async Task PasteClipboardImageToFolder(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
            {
                await ShowToast("目标目录不存在", ToastType.Error);
                return;
            }

            var counters = new SaveCounters();

            try
            {
                var handledClipboardContent = false;

                // 优先处理剪贴板中的文件列表，这样可以保留原始文件名
                if (Clipboard.ContainsFileDropList())
                {
                    var fileList = Clipboard.GetFileDropList();
                    foreach (var filePath in fileList.Cast<string>())
                    {
                        if (!File.Exists(filePath))
                            continue;

                        // 先按扩展名粗筛，避免把大体积非图片文件整体读入内存后才判为无效
                        if (!IsLikelyImageFile(filePath))
                            continue;

                        var fileName = Path.GetFileName(filePath);
                        var bytes = await File.ReadAllBytesAsync(filePath);
                        counters.Apply(await SaveImageToFolder(targetPath, fileName, bytes));

                        handledClipboardContent = true;
                    }
                }

                // 如果剪贴板里没有文件，再尝试读取直接复制的位图数据
                if (!handledClipboardContent && Clipboard.ContainsImage())
                {
                    var bitmapSource = Clipboard.GetImage();
                    if (bitmapSource != null)
                    {
                        // 位图数据统一编码为PNG，后续仍会经过格式检测与命名规则处理
                        var pngBytes = EncodeBitmapSourceToPng(bitmapSource);
                        var clipboardFileName = $"{AutoNamePrefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                        counters.Apply(await SaveImageToFolder(targetPath, clipboardFileName, pngBytes));

                        handledClipboardContent = true;
                    }
                }

                if (!handledClipboardContent)
                {
                    await ShowToast("剪贴板中没有可添加的图片", ToastType.Info);
                    return;
                }

                await ShowToast(counters.BuildToastMessage(), counters.HasSuccess ? ToastType.Success : ToastType.Info);

                if (counters.HasSuccess)
                {
                    QueueEmojiReload();
                }
            }
            catch (Exception ex)
            {
                await ShowToast($"粘贴失败: {ex.Message}", ToastType.Error);
            }
        }

        private async Task ShowFolderContextMenu(string targetPath, double x, double y, int requestId)
        {
            var canPasteImage = false;
            if (!string.IsNullOrWhiteSpace(targetPath) && Directory.Exists(targetPath))
            {
                try
                {
                    canPasteImage = HasClipboardImageContent();
                }
                catch (Exception ex)
                {
                    // 剪贴板被其他程序占用时通知用户，但不阻止菜单弹出
                    await ShowToast($"无法读取剪贴板: {ex.Message}", ToastType.Error);
                }
            }

            await WebView.CoreWebView2.ExecuteScriptAsync(
                $"handleMessage({{type: 'showFolderContextMenu', x: {x}, y: {y}, canPasteImage: {canPasteImage.ToString().ToLower()}, requestId: {requestId}}})");
        }

        private async Task<SaveOutcome> SaveImageToFolder(
            string targetPath,
            string originalName,
            byte[] bytes)
        {
            // 检测文件的实际图像格式
            var actualFormat = ImageFormatDetector.DetectImageFormat(bytes);
            if (actualFormat == null)
            {
                // 不是有效的图像文件，跳过
                return new SaveOutcome(false, false, false, false, true);
            }

            // 获取原始文件名（不含扩展名）
            var safeFileName = Path.GetFileName(originalName);
            var originalNameWithoutExt = Path.GetFileNameWithoutExtension(safeFileName);
            if (string.IsNullOrWhiteSpace(originalNameWithoutExt))
            {
                originalNameWithoutExt = $"{AutoNamePrefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
            }

            var originalExt = Path.GetExtension(safeFileName).TrimStart('.').ToLower();

            // 确定最终的文件名（使用正确的扩展名）
            var finalFileName = $"{originalNameWithoutExt}.{actualFormat}";
            var destPath = Path.Combine(targetPath, finalFileName);

            // 记录是否进行了格式修正
            var isFormatCorrected = !string.IsNullOrEmpty(originalExt) &&
                                    originalExt != actualFormat &&
                                    originalExt != "null"; // QQNT可能生成.null文件
            var renamed = false;

            // 检查文件是否已存在（使用正确的扩展名）
            if (File.Exists(destPath))
            {
                // 如果是MD5文件名且文件已存在，跳过
                if (IsMd5FileName(originalNameWithoutExt))
                {
                    // 重复跳过的文件没有真正写入，不应计为格式修正
                    return new SaveOutcome(false, true, false, false, false);
                }

                // 非MD5文件名，添加数字后缀
                var counter = 1;
                while (File.Exists(destPath))
                {
                    destPath = Path.Combine(targetPath, $"{originalNameWithoutExt}_{counter}.{actualFormat}");
                    counter++;
                }

                renamed = true;
            }

            // 写入文件
            await File.WriteAllBytesAsync(destPath, bytes);
            return new SaveOutcome(true, false, renamed, isFormatCorrected, false);
        }

        // 无来源文件名时统一使用的前缀（拖拽/剪贴板/位图均一致）
        private const string AutoNamePrefix = "image";

        // 按扩展名粗筛常见图片格式，避免把大体积非图片文件整体读入内存后才判为无效
        // .null 一并纳入，因为 QQNT 复制出的图片文件可能带该扩展名
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".null"
        };

        private static bool IsLikelyImageFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return ImageExtensions.Contains(ext);
        }

        private static bool HasClipboardImageContent()
        {
            if (Clipboard.ContainsFileDropList())
            {
                var fileList = Clipboard.GetFileDropList();
                foreach (var filePath in fileList.Cast<string>())
                {
                    if (File.Exists(filePath) && IsLikelyImageFile(filePath))
                    {
                        return true;
                    }
                }
            }

            return Clipboard.ContainsImage();
        }

        // 统一收集图片保存结果并构建提示信息，消除各导入入口的计数与文案样板
        private sealed class SaveCounters
        {
            public int Success;
            public int Skipped;
            public int Renamed;
            public int FormatCorrected;
            public int Invalid;

            public void Apply(SaveOutcome outcome)
            {
                if (outcome.Success) Success++;
                if (outcome.Skipped) Skipped++;
                if (outcome.Renamed) Renamed++;
                if (outcome.FormatCorrected) FormatCorrected++;
                if (outcome.Invalid) Invalid++;
            }

            public bool HasSuccess => Success > 0;

            public string BuildToastMessage()
            {
                var messages = new List<string>();
                if (Success > 0) messages.Add($"{Success} 个文件");
                if (FormatCorrected > 0) messages.Add($"{FormatCorrected} 个格式修正");
                if (Skipped > 0) messages.Add($"{Skipped} 个重复");
                if (Renamed > 0) messages.Add($"{Renamed} 个重命名");
                if (Invalid > 0) messages.Add($"{Invalid} 个无效");

                return messages.Count > 0
                    ? $"添加完成：{string.Join("，", messages)}"
                    : "没有添加任何文件";
            }
        }

        private record SaveOutcome(bool Success, bool Skipped, bool Renamed, bool FormatCorrected, bool Invalid);

        private static byte[] EncodeBitmapSourceToPng(System.Windows.Media.Imaging.BitmapSource bitmapSource)
        {
            // 统一编码为PNG，避免不同剪贴板来源带来的格式兼容差异
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapSource));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private void TogglePin()
        {
            _isPinned = !_isPinned;
            _settings.IsPinned = _isPinned;
            Topmost = _isPinned; // 根据钉住状态设置窗口置顶
            _ = UpdatePinnedState();
        }

        private async Task UpdatePinnedState()
        {
            await WebView.CoreWebView2.ExecuteScriptAsync($"updatePinnedState({_isPinned.ToString().ToLower()})");
        }

        private async Task CopyImageToClipboard(string imagePath)
        {
            try
            {
                var dataObject = new DataObject();
                var fileList = new System.Collections.Specialized.StringCollection { imagePath };
                dataObject.SetFileDropList(fileList);

                if (_settings.EnableLegacyClipboardCompatibility)
                {
                    // 设置图像数据以确保怀旧版QQ能正确处理
                    try
                    {
                        await using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
                        var bitmapImage = new System.Windows.Media.Imaging.BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.StreamSource = stream;
                        bitmapImage.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        // 添加 IgnoreColorProfile 选项来忽略 ICC profile
                        bitmapImage.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();
                        dataObject.SetImage(bitmapImage);
                    }
                    catch
                    {
                        // 如果加载图像失败，仍然可以使用文件列表方式
                        // 大多数程序都支持从文件列表粘贴
                    }
                }

                // 设置剪贴板（copy=false）
                Clipboard.SetDataObject(dataObject, false);

                await ShowToast("表情已复制到剪贴板", ToastType.Success);
            }
            catch
            {
                // 忽略剪贴板API异常，通常数据已经成功写入
                await ShowToast("表情已复制到剪贴板", ToastType.Success);
            }
        }

        private void RestoreFocusAndPaste()
        {
            if (_lastActiveWindow != IntPtr.Zero)
            {
                // 还原焦点到之前的窗口
                SetForegroundWindow(_lastActiveWindow);

                // 检查是否是QQ窗口
                if (IsQQWindow(_lastActiveWindow))
                {
                    // 延迟一下确保焦点切换完成
                    Task.Delay(100).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            // 发送 Ctrl+V
                            keybd_event(VkControl, 0, 0, UIntPtr.Zero);
                            keybd_event(VkV, 0, 0, UIntPtr.Zero);
                            keybd_event(VkV, 0, KeyeventfKeyup, UIntPtr.Zero);
                            keybd_event(VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
                        });
                    });
                }
            }
        }

        /// <summary>
        /// 在资源管理器中打开文件位置
        /// </summary>
        /// <param name="filePath">文件路径</param>
        private void OpenFileLocation(string filePath)
        {
            try
            {
                if (!Path.Exists(filePath))
                {
                    _ = ShowToast("文件不存在", ToastType.Error);
                    return;
                }

                // 使用explorer.exe的/select参数来选中文件
                // 这个方法兼容大多数第三方文件管理器，因为它们通常会接管这个命令
                var startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                };

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                // 如果explorer.exe失败，尝试直接打开包含目录
                try
                {
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = directory,
                            UseShellExecute = true
                        });
                    }
                }
                catch
                {
                    _ = ShowToast($"无法打开文件位置: {ex.Message}", ToastType.Error);
                }
            }
        }

        /// <summary>
        /// 删除图片文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        private async Task DeleteImageFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    await ShowToast("文件不存在", ToastType.Error);
                    return;
                }

                // 获取文件名用于确认对话框
                var fileName = Path.GetFileName(filePath);

                // 显示确认对话框
                var result = MessageBox.Show(
                    $"确定要删除这个表情吗？\n\n文件: {fileName}\n\n此操作不可撤销。",
                    "确认删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No); // 默认选择"否"，更安全

                if (result != MessageBoxResult.Yes)
                {
                    return; // 用户取消删除
                }

                // 删除文件
                File.Delete(filePath);

                // 从最近使用列表中移除（如果存在）
                _settings.RemoveRecentEmoji(filePath);
                _settings.Save();

                // 刷新表情数据
                QueueEmojiReload();

                await ShowToast("文件已删除", ToastType.Success);
            }
            catch (Exception ex)
            {
                await ShowToast($"删除失败: {ex.Message}", ToastType.Error);
            }
        }

        private static bool IsQQWindow(IntPtr hWnd)
        {
            try
            {
                GetWindowThreadProcessId(hWnd, out var processId);
                var process = Process.GetProcessById((int)processId);
                var processName = process.ProcessName.ToLower();

                // 检查各种QQ相关进程
                return processName.Contains("qq") &&
                       !processName.Contains("qqmusic") &&
                       !processName.Contains("qqbrowser");
            }
            catch
            {
                return false;
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int wmHotkey = 0x0312;

            if (msg == wmHotkey && wParam.ToInt32() == HotkeyId)
            {
                if (_isVisible)
                {
                    HideWindow();
                }
                else
                {
                    ShowWindow();
                }
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void ShowWindow()
        {
            _lastActiveWindow = GetForegroundWindow();
            Show();
            Activate();
            WebView.Focus();
            _isVisible = true;
        }

        private void ShowWindowFromTray()
        {
            // 从托盘显示窗口，不重新获取前台窗口（已在点击事件中获取）
            Show();
            Activate();
            WebView.Focus();
            _isVisible = true;
        }

        private void HideWindow()
        {
            Hide();
            _isVisible = false;

            // 如果是点击表情后隐藏窗口，执行粘贴操作
            if (_shouldPasteAfterDeactivate)
            {
                RestoreFocusAndPaste();
                _shouldPasteAfterDeactivate = false; // 重置标志
            }
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            SaveWindowState();

            // 如果钉住了，不自动隐藏
            if (_isPinned)
                return;

            // 延迟一下再隐藏，避免点击时立即隐藏
            Task.Delay(100).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isVisible && !IsActive && !_isPinned)
                    {
                        HideWindow();
                    }
                });
            });
        }

        private void OpenSettingsWindow()
        {
            var settingsWindow = new SettingsWindow(_settings)
            {
                Owner = this
            };

            if (settingsWindow.ShowDialog() == true)
            {
                // 获取更新后的设置
                _settings = settingsWindow.GetSettings();

                // 应用新设置
                ApplySettings();
            }
        }

        private void ApplySettings()
        {
            // 重新初始化文件监听器
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            InitializeFileWatcher();

            // 重新注册热键
            RegisterHotkey();

            // 重新设置虚拟主机映射和加载数据
            Task.Run(async () =>
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    // 清除WebView2缓存并重新加载
                    await RefreshWebViewWithNewPath();
                });
            });
        }

        /// <summary>
        /// 刷新WebView2并使用新的路径设置
        /// </summary>
        private async Task RefreshWebViewWithNewPath()
        {
            if (WebView?.CoreWebView2 == null)
                return;

            try
            {
                // 清除缓存（可选，但有助于确保干净的状态）
                await WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
            }
            catch
            {
                // 忽略清除缓存失败的错误
            }

            try
            {
                // 重新设置虚拟主机映射
                await SetupVirtualHostMapping();

                // 重新加载HTML内容以确保使用新的映射
                var htmlContent = await GetHtmlContent();
                WebView.NavigateToString(htmlContent);

                // 等待页面加载完成后重新加载表情数据
                // 使用一次性事件处理器
                void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    WebView.NavigationCompleted -= OnNavigationCompleted;
                    if (e.IsSuccess)
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            QueueEmojiReload();
                            _ = UpdatePinnedState();
                        });
                    }
                }

                WebView.NavigationCompleted += OnNavigationCompleted;
            }
            catch (Exception ex)
            {
                await ShowToast($"刷新失败: {ex.Message}", ToastType.Error);

                // 如果刷新失败，至少尝试重新加载数据
                await Task.Delay(200);
                QueueEmojiReload();
            }
        }

        private void SaveWindowState()
        {
            try
            {
                // 总是保存窗口状态
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
                _settings.WindowState = WindowState;
                _settings.Save();
            }
            catch
            {
                // 如果保存失败，忽略错误
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 取消关闭事件，改为隐藏窗口
            e.Cancel = true;
            HideWindow();
        }

        private void Window_LocationChanged(object sender, EventArgs e)
        {
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsWindow();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 仅隐藏窗口，不退出程序
            HideWindow();
        }

        private void ExitApplication()
        {
            var result = MessageBox.Show(
                this, // 指定父窗口
                "确定要退出表情管理器吗？\n程序将完全关闭，需要手动重新启动。",
                "确认退出",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No); // 默认选择"否"

            if (result == MessageBoxResult.Yes)
            {
                // 清理资源
                CleanupResources();

                // 退出应用程序
                Application.Current.Shutdown();
            }
        }

        private void CleanupResources()
        {
            try
            {
                // 保存窗口状态
                SaveWindowState();

                // 注销热键
                if (_source != null)
                {
                    UnregisterHotKey(_source.Handle, HotkeyId);
                }

                // 释放文件监听器
                _fileWatcher?.Dispose();

                // 释放托盘图标
                _taskbarIcon?.Dispose();

                // 停止前台窗口追踪
                _foregroundWindowTracker?.Stop();
                _foregroundWindowTracker = null;

                // 取消可能的刷新任务
                _reloadCts?.Cancel();
                _reloadCts?.Dispose();
                _reloadCts = null;
            }
            catch
            {
                // 忽略清理过程中的错误
            }
        }

        private static string GetEmbeddedHtml()
        {
            // 作为后备，保留一个最小化的内嵌HTML
            return """
                   <!DOCTYPE html>
                   <html>
                   <head>
                       <meta charset='utf-8'>
                       <style>
                           body {
                               font-family: 'Microsoft YaHei', Arial, sans-serif;
                               background: #1e1e1e;
                               color: #e0e0e0;
                               display: flex;
                               align-items: center;
                               justify-content: center;
                               height: 100vh;
                               margin: 0;
                           }
                       </style>
                   </head>
                   <body>
                       <div>请确保 EmojiManager.html 文件存在于程序目录中</div>
                   </body>
                   </html>
                   """;
        }

        /// <summary>
        /// 加载所有文件夹的缩放配置
        /// </summary>
        private static Dictionary<string, double> LoadAllFolderScales(string basePath)
        {
            var scales = new Dictionary<string, double>();
            
            if (!Directory.Exists(basePath))
                return scales;
            
            try
            {
                // 递归加载所有文件夹的缩放配置
                LoadFolderScalesRecursive(basePath, scales);
            }
            catch { }
            
            return scales;
        }

        private static void LoadFolderScalesRecursive(string path, Dictionary<string, double> scales)
        {
            try
            {
                // 检查当前文件夹的缩放配置
                var scaleFile = Path.Combine(path, "emoji_scale.json");
                if (File.Exists(scaleFile))
                {
                    try
                    {
                        var json = File.ReadAllText(scaleFile);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("scale", out var scaleElement))
                        {
                            scales[path] = scaleElement.GetDouble();
                        }
                    }
                    catch { }
                }
                
                // 递归处理子文件夹
                foreach (var dir in Directory.GetDirectories(path))
                {
                    LoadFolderScalesRecursive(dir, scales);
                }
            }
            catch { }
        }

        /// <summary>
        /// 保存文件夹的缩放配置
        /// </summary>
        private static void SaveFolderScale(string folderPath, double scale)
        {
            try
            {
                // 验证路径是否存在
                if (!Directory.Exists(folderPath))
                {
                    Console.WriteLine($"Folder not found: {folderPath}");
                    return;
                }
                
                var scaleFile = Path.Combine(folderPath, "emoji_scale.json");
                var data = new { scale };
                var json = JsonSerializer.Serialize(data, JsonOptions);
                File.WriteAllText(scaleFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save folder scale: {ex.Message}");
            }
        }

        /// <summary>
        /// 删除文件夹的缩放配置
        /// </summary>
        private static void DeleteFolderScale(string folderPath)
        {
            try
            {
                // 验证路径是否存在
                if (!Directory.Exists(folderPath))
                {
                    Console.WriteLine($"Folder not found: {folderPath}");
                    return;
                }

                var scaleFile = Path.Combine(folderPath, "emoji_scale.json");
                if (File.Exists(scaleFile))
                {
                    File.Delete(scaleFile);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete folder scale: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载指定目录下子文件夹的自定义显示顺序。
        /// 返回文件夹名有序列表；配置不存在或为空时返回空列表（调用方按文件系统默认顺序）。
        /// </summary>
        private static List<string> LoadFolderOrder(string parentPath)
        {
            var order = new List<string>();

            if (!Directory.Exists(parentPath))
                return order;

            try
            {
                var orderFile = Path.Combine(parentPath, "emoji_folder_order.json");
                if (File.Exists(orderFile))
                {
                    var json = File.ReadAllText(orderFile);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("folders", out var foldersElement))
                    {
                        foreach (var name in foldersElement.EnumerateArray())
                        {
                            var s = name.GetString();
                            if (!string.IsNullOrEmpty(s))
                                order.Add(s);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load folder order: {ex.Message}");
            }

            return order;
        }

        /// <summary>
        /// 保存指定目录下子文件夹的自定义显示顺序到 emoji_folder_order.json
        /// </summary>
        private static void SaveFolderOrder(string parentPath, List<string> folderOrder)
        {
            try
            {
                if (!Directory.Exists(parentPath))
                {
                    Console.WriteLine($"Parent folder not found: {parentPath}");
                    return;
                }

                var orderFile = Path.Combine(parentPath, "emoji_folder_order.json");
                var data = new { folders = folderOrder };
                var json = JsonSerializer.Serialize(data, JsonOptions);
                File.WriteAllText(orderFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save folder order: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载指定文件夹内图片的自定义显示顺序。
        /// 返回文件名有序列表；配置不存在时返回空列表（调用方按默认顺序）。
        /// </summary>
        private static List<string> LoadImageOrder(string folderPath)
        {
            var order = new List<string>();

            if (!Directory.Exists(folderPath))
                return order;

            try
            {
                var orderFile = Path.Combine(folderPath, "emoji_image_order.json");
                if (File.Exists(orderFile))
                {
                    var json = File.ReadAllText(orderFile);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("images", out var imagesElement))
                    {
                        foreach (var name in imagesElement.EnumerateArray())
                        {
                            var s = name.GetString();
                            if (!string.IsNullOrEmpty(s))
                                order.Add(s);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load image order: {ex.Message}");
            }

            return order;
        }

        /// <summary>
        /// 保存指定文件夹内图片的自定义显示顺序到 emoji_image_order.json
        /// </summary>
        private static void SaveImageOrder(string folderPath, List<string> imageOrder)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    Console.WriteLine($"Folder not found: {folderPath}");
                    return;
                }

                var orderFile = Path.Combine(folderPath, "emoji_image_order.json");
                var data = new { images = imageOrder };
                var json = JsonSerializer.Serialize(data, JsonOptions);
                File.WriteAllText(orderFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save image order: {ex.Message}");
            }
        }

        /// <summary>
        /// 递归加载所有文件夹的图片备注，聚合为 (图片绝对路径 -> 备注) 字典
        /// </summary>
        private static Dictionary<string, string> LoadAllFolderRemarks(string basePath)
        {
            var remarks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(basePath))
                return remarks;

            try
            {
                LoadFolderRemarksRecursive(basePath, remarks);
            }
            catch { }

            return remarks;
        }

        private static void LoadFolderRemarksRecursive(string path, Dictionary<string, string> remarks)
        {
            try
            {
                var remarkFile = Path.Combine(path, "emoji_remarks.json");
                if (File.Exists(remarkFile))
                {
                    try
                    {
                        var json = File.ReadAllText(remarkFile);
                        var fileRemarks = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
                        if (fileRemarks != null)
                        {
                            foreach (var kvp in fileRemarks)
                            {
                                var absPath = Path.Combine(path, kvp.Key);
                                remarks[absPath] = kvp.Value;
                            }
                        }
                    }
                    catch { }
                }

                foreach (var dir in Directory.GetDirectories(path))
                {
                    LoadFolderRemarksRecursive(dir, remarks);
                }
            }
            catch { }
        }

        /// <summary>
        /// 同步图片重命名导致的备注键名变化。仅处理同文件夹内改名（修扩展名场景）。
        /// 旧文件无备注则不操作；目标已有备注则保留目标不覆盖。
        /// </summary>
        private static void RenameImageRemark(string oldImagePath, string newImagePath)
        {
            try
            {
                var folderPath = Path.GetDirectoryName(oldImagePath);
                if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                    return;

                if (!string.Equals(folderPath, Path.GetDirectoryName(newImagePath), StringComparison.OrdinalIgnoreCase))
                    return;

                var remarkFile = Path.Combine(folderPath, "emoji_remarks.json");
                if (!File.Exists(remarkFile)) return;

                Dictionary<string, string>? existing;
                try
                {
                    var existingJson = File.ReadAllText(remarkFile);
                    existing = JsonSerializer.Deserialize<Dictionary<string, string>>(existingJson, JsonOptions);
                }
                catch { return; }

                if (existing == null || existing.Count == 0) return;

                var remarks = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
                var oldName = Path.GetFileName(oldImagePath);
                var newName = Path.GetFileName(newImagePath);

                if (!remarks.TryGetValue(oldName, out var remark)) return;

                remarks.Remove(oldName);
                if (!remarks.ContainsKey(newName))
                {
                    remarks[newName] = remark;
                }

                if (remarks.Count == 0)
                {
                    File.Delete(remarkFile);
                }
                else
                {
                    var json = JsonSerializer.Serialize(remarks, JsonOptions);
                    File.WriteAllText(remarkFile, json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to rename image remark: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存或清除单张图片的备注。备注为空时移除条目；当所在文件夹没有任何备注时删除整个 json 文件。
        /// </summary>
        private static void SaveImageRemark(string imagePath, string remark)
        {
            try
            {
                var folderPath = Path.GetDirectoryName(imagePath);
                if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                {
                    Console.WriteLine($"Folder not found for image: {imagePath}");
                    return;
                }

                var fileName = Path.GetFileName(imagePath);
                var remarkFile = Path.Combine(folderPath, "emoji_remarks.json");

                var fileRemarks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(remarkFile))
                {
                    try
                    {
                        var existingJson = File.ReadAllText(remarkFile);
                        var existing = JsonSerializer.Deserialize<Dictionary<string, string>>(existingJson, JsonOptions);
                        if (existing != null)
                        {
                            foreach (var kvp in existing)
                                fileRemarks[kvp.Key] = kvp.Value;
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(remark))
                {
                    fileRemarks.Remove(fileName);
                }
                else
                {
                    fileRemarks[fileName] = remark;
                }

                if (fileRemarks.Count == 0)
                {
                    if (File.Exists(remarkFile))
                        File.Delete(remarkFile);
                }
                else
                {
                    var json = JsonSerializer.Serialize(fileRemarks, JsonOptions);
                    File.WriteAllText(remarkFile, json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save image remark: {ex.Message}");
            }
        }

        /// <summary>
        /// 递归扫描所有子文件夹的 emoji_remarks.json，移除对应图片文件不存在的条目。
        /// 若清理后字典为空则删除整个 json 文件。
        /// </summary>
        /// <returns>(清理的备注条数, 删除的空备注文件数, 处理失败的文件数)</returns>
        public static async Task<(int orphansRemoved, int filesDeleted, int errors)> CleanupOrphanedRemarks(string rootPath)
        {
            var orphansRemoved = 0;
            var filesDeleted = 0;
            var errors = 0;

            if (!Directory.Exists(rootPath))
                return (0, 0, 0);

            await Task.Run(() =>
            {
                Process(rootPath, ref orphansRemoved, ref filesDeleted, ref errors);
            });

            return (orphansRemoved, filesDeleted, errors);

            static void Process(string directory, ref int removed, ref int deleted, ref int errCount)
            {
                try
                {
                    var remarkFile = Path.Combine(directory, "emoji_remarks.json");
                    if (File.Exists(remarkFile))
                    {
                        try
                        {
                            var json = File.ReadAllText(remarkFile);
                            var existing = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);

                            if (existing == null || existing.Count == 0)
                            {
                                File.Delete(remarkFile);
                                deleted++;
                            }
                            else
                            {
                                var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var kvp in existing)
                                {
                                    var imgPath = Path.Combine(directory, kvp.Key);
                                    if (File.Exists(imgPath))
                                    {
                                        kept[kvp.Key] = kvp.Value;
                                    }
                                    else
                                    {
                                        removed++;
                                    }
                                }

                                if (kept.Count == 0)
                                {
                                    File.Delete(remarkFile);
                                    deleted++;
                                }
                                else if (kept.Count != existing.Count)
                                {
                                    var newJson = JsonSerializer.Serialize(kept, JsonOptions);
                                    File.WriteAllText(remarkFile, newJson);
                                }
                            }
                        }
                        catch
                        {
                            errCount++;
                        }
                    }

                    foreach (var subdir in Directory.GetDirectories(directory))
                    {
                        Process(subdir, ref removed, ref deleted, ref errCount);
                    }
                }
                catch
                {
                    errCount++;
                }
            }
        }
    }

    public readonly struct ImageCorrectionProgress
    {
        public ImageCorrectionProgress(int processed, int total)
        {
            Processed = processed;
            Total = total;
        }

        public int Processed { get; }
        public int Total { get; }
    }

    public class EmojiFolder
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public List<string> Images { get; set; } = [];
        public List<EmojiFolder> Children { get; set; } = [];
    }

    public class WebMessage
    {
        public string Type { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public List<FileData> Files { get; set; } = [];
        public string TargetPath { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public int RequestId { get; set; }
    }

    public class FileData
    {
        public string Name { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty; // Base64编码的文件内容
    }

    public enum ToastType
    {
        Success,
        Error,
        Info
    }
}
