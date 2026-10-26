// MemoryGallery: a local desktop photo clothesline for Windows. MIT License.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Path = System.IO.Path;

[assembly: System.Reflection.AssemblyTitle("拾光 - 桌面相册")]
[assembly: System.Reflection.AssemblyDescription("Private, local desktop photo clothesline for Windows")]
[assembly: System.Reflection.AssemblyVersion("0.1.0.0")]

namespace MemoryGallery
{
    static class Native
    {
        internal const int Hotkey = 0x0312, MouseActivate = 0x0021;
        internal static readonly uint ShowMessage = RegisterWindowMessage("MemoryGallery.Windows.Show.v1");


        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr h, StringBuilder name, int count);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rect r, int size);
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    }

    [DataContract] sealed class Photo
    {
        [DataMember] public string File;
        [DataMember] public string OriginalFile;
        [DataMember] public bool IsReference;
        [DataMember] public string Digest;
        [DataMember] public string Label;
        [DataMember] public string Remark;
        [DataMember] public DateTime Created;
        internal BitmapSource Thumb;
    }
    sealed class PreparedPhoto
    {
        internal string Original, Digest, Label;
        internal DateTime Created;
        internal BitmapSource Thumb;
    }

    [DataContract] sealed class Preferences
    {
        [DataMember] public bool WelcomeShown;
        [DataMember] public bool StartupEnabled;
        [DataMember] public int StartupRevision;
        [DataMember] public string EntryPosition = "Center";
        [DataMember] public double EntryPositionRatio = .5;
        [DataMember] public int EntryPositionRevision;
    }

    sealed partial class Store
    {
        internal readonly string Root, Inbox;
        internal List<Photo> Items = new List<Photo>();
        internal Preferences Settings = new Preferences();
        internal const int MaxItems = 300;
        internal Action Changed;
        internal Store(string root)
        {
            Root = Path.GetFullPath(root);
            Inbox = Path.Combine(Root, "Inbox");
            Directory.CreateDirectory(Inbox);
            Settings = Read<Preferences>(Path.Combine(Root, "settings.json")) ?? new Preferences();
            if (Settings.EntryPosition != "Left" && Settings.EntryPosition != "Center" && Settings.EntryPosition != "Right") Settings.EntryPosition = "Center";
            if (Settings.EntryPositionRevision < 1)
            {
                Settings.EntryPositionRatio = Settings.EntryPosition == "Left" ? .1 : Settings.EntryPosition == "Right" ? .9 : .5;
                Settings.EntryPositionRevision = 1;
            }
            Settings.EntryPositionRatio = Math.Max(0, Math.Min(1, Settings.EntryPositionRatio));
            RestoreNotes();
            var saved = Read<List<Photo>>(Path.Combine(Root, "items.json")) ?? new List<Photo>();
            bool lazyThumbnails = saved.Count > 40;
            foreach (var photo in saved.Take(MaxItems))
            {
                try
                {
                    if (PathAvailable(photo))
                    {
                        // Decode only a thumbnail on startup. Loading and hashing every full-resolution
                        // original here can retain hundreds of MB until the next full GC.
                        if (!lazyThumbnails) photo.Thumb = LoadThumbnail(FullPath(photo));
                        photo.Remark = photo.Remark ?? "";
                        Items.Add(photo);
                    }
                }
                catch (Exception ex) { Log("Restore image", ex); }
            }
        }
        static T Read<T>(string file) where T : class
        {
            if (!System.IO.File.Exists(file)) return null;
            try { using (var s = System.IO.File.OpenRead(file)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(s); }
            catch { return null; }
        }
        static void Write<T>(string file, T value)
        {
            string temp = file + ".tmp";
            using (var s = System.IO.File.Create(temp)) new DataContractJsonSerializer(typeof(T)).WriteObject(s, value);
            if (!System.IO.File.Exists(file)) { System.IO.File.Move(temp, file); return; }
            try { System.IO.File.Replace(temp, file, null); }
            catch (UnauthorizedAccessException) { System.IO.File.Copy(temp, file, true); System.IO.File.Delete(temp); }
            catch (PlatformNotSupportedException) { System.IO.File.Copy(temp, file, true); System.IO.File.Delete(temp); }
        }
        internal void SaveSettings() { Write(Path.Combine(Root, "settings.json"), Settings); }
        internal void Save() { Write(Path.Combine(Root, "items.json"), Items); }
        internal void Log(string context, Exception error)
        {
            try { System.IO.File.AppendAllText(Path.Combine(Root, "error.log"), DateTime.Now.ToString("s") + " " + context + ": " + error.Message + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
        internal bool IsSafe(string file)
        {
            return !String.IsNullOrWhiteSpace(file) && file == Path.GetFileName(file) &&
                file.StartsWith("snap-", StringComparison.Ordinal) && file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }
        internal string FullPath(Photo photo)
        {
            if (photo.IsReference && !String.IsNullOrWhiteSpace(photo.OriginalFile)) return Path.GetFullPath(photo.OriginalFile);
            if (!IsSafe(photo.File)) throw new InvalidOperationException("Invalid cache filename");
            return Path.Combine(Inbox, photo.File);
        }
        internal bool PathAvailable(Photo photo)
        {
            try { return photo != null && System.IO.File.Exists(FullPath(photo)); }
            catch { return false; }
        }
        internal static BitmapSource Load(string file)
        {
            if (new FileInfo(file).Length > 64L * 1024 * 1024) throw new InvalidOperationException("图片超过 64 MB。");
            using (var stream = System.IO.File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                CheckSize(frame);
                frame.Freeze();
                return frame;
            }
        }
        internal static BitmapSource Preview(string file, int maxWidth, int maxHeight, double aspect)
        {
            if (new FileInfo(file).Length > 64L * 1024 * 1024) throw new InvalidOperationException("图片超过 64 MB。");
            using (var stream = System.IO.File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                if (aspect >= maxWidth / (double)maxHeight) image.DecodePixelWidth = Math.Max(1, maxWidth);
                else image.DecodePixelHeight = Math.Max(1, maxHeight);
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
        internal static BitmapSource LoadThumbnail(string file)
        {
            if (new FileInfo(file).Length > 64L * 1024 * 1024) throw new InvalidOperationException("图片超过 64 MB。");
            using (var stream = System.IO.File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 360; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }
        }
        internal BitmapSource EnsureThumbnail(Photo photo)
        {
            if (photo.Thumb != null) return photo.Thumb;
            try { photo.Thumb = LoadThumbnail(FullPath(photo)); }
            catch (Exception ex)
            {
                Log("Load thumbnail", ex);
                byte[] pixel = { 210, 218, 224, 255 };
                photo.Thumb = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixel, 4);
                photo.Thumb.Freeze();
            }
            return photo.Thumb;
        }
        internal void ReleaseThumbnailsExcept(HashSet<Photo> keep)
        {
            if (Items.Count <= 40) return;
            foreach (var photo in Items) if (!keep.Contains(photo)) photo.Thumb = null;
        }
        static void CheckSize(BitmapSource image)
        {
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0 || (long)image.PixelWidth * image.PixelHeight > 50000000)
                throw new InvalidOperationException("图片尺寸过大（最多 5000 万像素）。");
        }
        internal static string Hash(BitmapSource image)
        {
            CheckSize(image);
            var rgba = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int stride = checked(rgba.PixelWidth * 4);
            byte[] bytes = new byte[checked(stride * rgba.PixelHeight)];
            rgba.CopyPixels(bytes, stride, 0);
            using (var sha = SHA256.Create())
            {
                byte[] dimensions = Encoding.ASCII.GetBytes(rgba.PixelWidth + "x" + rgba.PixelHeight + ":");
                sha.TransformBlock(dimensions, 0, dimensions.Length, dimensions, 0);
                sha.TransformFinalBlock(bytes, 0, bytes.Length);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        internal static BitmapSource Thumbnail(BitmapSource image)
        {
            double scale = Math.Min(1, 360.0 / Math.Max(image.PixelWidth, image.PixelHeight));
            var small = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            var converted = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            // A standalone thumbnail does not retain every full-resolution source in memory.
            var thumbnail = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            thumbnail.Freeze();
            return thumbnail;
        }
        internal Photo Add(BitmapSource image, string label)
        {
            if (Items.Count >= MaxItems) throw new InvalidOperationException("相册最多保存 300 张照片，请先移除一些照片再添加。");
            string hash = Hash(image);
            if (Items.Any(x => x.Digest == hash)) return null;
            string filename = "snap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png";
            string full = Path.Combine(Inbox, filename);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = System.IO.File.Create(full)) encoder.Save(stream);
            var photo = new Photo { File = filename, Digest = hash, Label = label, Created = DateTime.Now, Thumb = Thumbnail(image) };
            Items.Insert(0, photo);
            Save();
            if (Changed != null) Changed();
            return photo;
        }
        internal Photo Import(string file)
        {
            return CommitImport(PrepareImport(file));
        }
        internal PreparedPhoto PrepareImport(string file)
        {
            string original = Path.GetFullPath(file);
            var image = Load(original);
            return new PreparedPhoto { Original = original, Digest = Hash(image), Label = Path.GetFileName(original),
                Created = System.IO.File.GetLastWriteTime(original), Thumb = Thumbnail(image) };
        }
        internal Photo CommitImport(PreparedPhoto prepared)
        {
            if (Items.Count >= MaxItems) throw new InvalidOperationException("相册最多保存 300 张照片，请先移除一些照片再导入。");
            if (Items.Any(x => x.Digest == prepared.Digest)) return null;
            var photo = new Photo
            {
                File = "snap-ref-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".png",
                OriginalFile = prepared.Original, IsReference = true, Digest = prepared.Digest,
                Label = prepared.Label, Created = prepared.Created, Thumb = prepared.Thumb
            };
            Items.Insert(0, photo);
            Save();
            if (Changed != null) Changed();
            return photo;
        }
        void DeleteCache(Photo item)
        {
            try { if (!item.IsReference && IsSafe(item.File)) System.IO.File.Delete(FullPath(item)); }
            catch (Exception ex) { Log("Delete cached image", ex); }
        }
        internal void Remove(Photo item)
        {
            int index = Items.IndexOf(item); if (index < 0) return;
            Items.RemoveAt(index);
            try { Save(); } catch { Items.Insert(index, item); throw; }
            DeleteCache(item);
            if (Changed != null) Changed();
        }
        internal void Refresh(Photo item)
        {
            if (!Items.Contains(item)) return;
            string file = FullPath(item);
            if (!System.IO.File.Exists(file)) { Remove(item); return; }
            var image = Load(file);
            item.Digest = Hash(image);
            item.Thumb = Thumbnail(image);
            Save();
            if (Changed != null) Changed();
        }
        internal void Clear()
        {
            var removed = Items.ToList();
            Items.Clear();
            Save();
            foreach (var item in removed) DeleteCache(item);
            if (Changed != null) Changed();
        }
    }

    sealed partial class Shelf : Window
    {
        readonly Store store;
        readonly bool demo;
        readonly Canvas canvas = new Canvas();
        readonly TranslateTransform shelfSlide = new TranslateTransform();
        readonly TranslateTransform ropeSlide = new TranslateTransform();
        Canvas galleryLayer, itemLayer;
        List<object> ropeDeck;
        int virtualStart = -1, virtualEnd = -1;
        bool opening, collapsing, renderDeferred;
        internal const double ShelfHeight = 270, HiddenShelfOffset = -286;
        int transitionSerial;
        internal bool Collapsing { get { return collapsing; } }
        internal bool Opening { get { return opening; } }
        internal double ShelfOffset { get { return shelfSlide.Y; } }
        FrameworkElement indicator;
        FrameworkElement toastElement;
        readonly DispatcherTimer tick = new DispatcherTimer();

        Forms.NotifyIcon tray;
        Forms.ToolStripMenuItem pinMenu;
        internal Forms.ToolStripMenuItem startupMenu;
        internal StartupRegistration Startup;
        internal bool StartCollapsed;
        internal bool Expanded { get { return expanded; } }
        readonly SemaphoreSlim importGate = new SemaphoreSlim(1, 1);
        ToolTip helpTip, imageTip;
        Window largePreview;
        readonly Dictionary<string, BitmapSource> previewCache = new Dictionary<string, BitmapSource>();
        readonly Queue<string> previewCacheOrder = new Queue<string>();
        bool helpReading;
        IntPtr handle;
        HwndSource source;
        Forms.Screen screen;
        bool expanded, pinned, entryDragging, ropeDragging, dialogOpen, shuttingDown;
        DateTime keepUntil, hideUntil, lastInside, indicatorEntered;
        internal Func<IntPtr> ForegroundWindow = Native.GetForegroundWindow;
        internal Func<Native.Point?> PointerPosition = delegate { Native.Point point; return Native.GetCursorPos(out point) ? (Native.Point?)point : null; };

        double shelfWidth, ropeWidth, ropeOffset, ropeTarget, ropeDragOrigin, ropeDragStartX;
        int edgePanDirection;
        long ropeFrameTicks;
        DateTime wheelPauseUntil;
        bool ropeLayerCached;
        string toast = "";
        DateTime toastUntil;
        internal bool HotkeyRegistered;
        internal Shelf(Store data, bool demo)
        {
            store = data;
            this.demo = demo;
            Title = "拾光 · 桌面相册";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Height = 4; Width = 1000;
            canvas.RenderTransform = shelfSlide;
            Content = new Grid { Children = { canvas, motionLayer } };
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;
            int knownCount = store.Items.Count + store.Notes.Count;
            store.Changed = delegate { int count = store.Items.Count + store.Notes.Count; bool deckChanged = count != knownCount; knownCount = count; Render(deckChanged); };
            store.RemarkChanged = RefreshRemark;
            canvas.MouseWheel += delegate(object sender, MouseWheelEventArgs e) { if (expanded) { PanRope(e.Delta / 2.2); e.Handled = true; } };
            SourceInitialized += Initialize;
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (memoEditor != null && !memoEditor.FlushChanges()) e.Cancel = true; if (remarkEditor != null && !remarkEditor.TrySave()) e.Cancel = true; };
            Closed += Cleanup;
            AllowDrop = true;
            DragOver += delegate(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Drop += delegate(object sender, DragEventArgs e)
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                foreach (string file in (string[])e.Data.GetData(DataFormats.FileDrop)) Import(file);
                e.Handled = true;
            };
        }
        static SolidColorBrush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
        internal void Initialize(object sender, EventArgs e)
        {
            handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle);
            source.AddHook(Message);
            Native.SetWindowLong(handle, -20, Native.GetWindowLong(handle, -20) | 0x08000000 | 0x00000080);

            HotkeyRegistered = Native.RegisterHotKey(handle, 1, 0x4003, 0x54); // Ctrl+Alt+T, no repeat.
            screen = ScreenUnderPointer();
            MakeTray();
            if (Startup != null && !StartCollapsed)
            {
                string error;
                if (!Startup.Initialize(out error)) tray.ShowBalloonTip(5000, "开机启动未启用", error, Forms.ToolTipIcon.Warning);
            }
            ropeFrameTicks = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += AnimateRope;

            Position(false, screen);
            tick.Interval = TimeSpan.FromMilliseconds(80);
            tick.Tick += Tick;
            tick.Start();
            if (demo) { pinned = true; Reveal(); }
            else if (StartCollapsed) { Render(); }
            else
            {
                Reveal();
                keepUntil = DateTime.Now.AddSeconds(7);
                if (!store.Settings.WelcomeShown)
                {
                    tray.ShowBalloonTip(4500, "拾光相册已启动", "把鼠标移到屏幕顶部中央，晾衣绳就会滑下来。可从托盘导入照片。", Forms.ToolTipIcon.Info);
                    store.Settings.WelcomeShown = true;
                    store.SaveSettings();
                }
            }

            if (!HotkeyRegistered) StartupNotice("Ctrl + Alt + T 已被其他程序占用，可用顶部悬停或托盘展开。");
        }
        void StartupNotice(string message)
        {
            if (StartCollapsed) tray.ShowBalloonTip(5000, "拾光相册", message, Forms.ToolTipIcon.Warning);
            else Notify(message);
        }
        IntPtr Message(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.MouseActivate) { handled = true; return new IntPtr(3); }

            if (msg == Native.Hotkey)
            {
                if (w.ToInt32() == 1) { Toggle(); handled = true; }
            }
            if ((uint)msg == Native.ShowMessage) { ToggleFromTray(); handled = true; }
            return IntPtr.Zero;
        }

        Forms.Screen ScreenUnderPointer()
        {
            var point = PointerPosition();
            return point.HasValue ? Forms.Screen.FromPoint(new Drawing.Point(point.Value.X, point.Value.Y)) : (screen ?? Forms.Screen.PrimaryScreen);
        }
        double Dpi()
        {
            try { uint dpi = Native.GetDpiForWindow(handle); return dpi == 0 ? 1 : dpi / 96.0; }
            catch (EntryPointNotFoundException) { return source.CompositionTarget.TransformToDevice.M11; }
        }
        void Position(bool open, Forms.Screen target)
        {
            bool changed = screen == null || screen.DeviceName != target.DeviceName;
            screen = target;
            var area = screen.WorkingArea;
            double scale = Dpi();
            int height = (int)Math.Ceiling((open ? ShelfHeight : 8) * scale);
            Native.SetWindowPos(handle, new IntPtr(-1), area.Left, area.Top, area.Width, height, 0x0010);
            // A monitor move can change the WPF presentation source's DPI.
            scale = Dpi();
            int updated = (int)Math.Ceiling((open ? ShelfHeight : 8) * scale);
            if (height != updated) Native.SetWindowPos(handle, new IntPtr(-1), area.Left, area.Top, area.Width, updated, 0x0010);
            shelfWidth = area.Width / scale;
            canvas.Width = shelfWidth;
            canvas.Height = open ? ShelfHeight : 8;
        }
        internal Drawing.Rectangle IndicatorHitBounds()
        {
            if (indicator == null || expanded || !IsLoaded) return Drawing.Rectangle.Empty;
            var first = indicator.PointToScreen(new System.Windows.Point(0, 0));
            var last = indicator.PointToScreen(new System.Windows.Point(indicator.ActualWidth, indicator.ActualHeight));
            return Drawing.Rectangle.FromLTRB((int)Math.Round(first.X), (int)Math.Round(first.Y), (int)Math.Round(last.X), (int)Math.Round(last.Y));
        }
        bool Fullscreen(Forms.Screen target = null)
        {
            IntPtr foreground = ForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == handle || dialogOpen) return false;
            return HoverPolicy.Suppresses(foreground, (target ?? screen).Bounds);
        }
        void TryRevealFromIndicator()
        {
            if (expanded || collapsing || shuttingDown || dialogOpen || helpReading ||
                FlyingCount > 0 || DateTime.Now < hideUntil) return;
            var point = PointerPosition();
            if (!point.HasValue || !IndicatorHitBounds().Contains(point.Value.X, point.Value.Y)) return;
            if ((DateTime.Now - indicatorEntered).TotalMilliseconds < 260 || Mouse.LeftButton == MouseButtonState.Pressed) return;
            if (!demo && Fullscreen(ScreenUnderPointer())) return;
            Reveal();
        }
        void Tick(object sender, EventArgs e)
        {
            if (shuttingDown) return;
            if (dialogOpen || entryDragging || ropeDragging || helpReading) return;
            var point = PointerPosition(); if (!point.HasValue) return;
            var p = point.Value;
            Forms.Screen under = Forms.Screen.FromPoint(new Drawing.Point(p.X, p.Y));
            DateTime now = DateTime.Now;
            UpdateScreenEdgePan(p);
            if (FlyingCount > 0) { lastInside = now; return; }
            if (!demo && Fullscreen(under))
            {
                if (expanded && screen.DeviceName == under.DeviceName) Collapse(false, false);
                return;
            }
            if ((imageTip != null && imageTip.IsOpen) || (noteTip != null && noteTip.IsOpen)) { lastInside = now; return; }
            if (collapsing) return;
            if (!expanded) TryRevealFromIndicator();
            bool inside = expanded && p.X >= screen.WorkingArea.Left && p.X < screen.WorkingArea.Right &&
                p.Y >= screen.WorkingArea.Top && p.Y < screen.WorkingArea.Top + 266 * Dpi();
            if (inside) lastInside = now;
            if (expanded && !pinned && now > keepUntil && (now - lastInside).TotalMilliseconds > 700) Collapse(false);
            if (toast != "" && now > toastUntil) { toast = ""; UpdateToast(); }
        }
        internal void Reveal()
        {
            bool was = expanded;
            bool reversing = collapsing;
            double offset = shelfSlide.Y;
            int serial = ++transitionSerial; collapsing = false;
            shelfSlide.BeginAnimation(TranslateTransform.YProperty, null);
            var target = ScreenUnderPointer();
            bool moved = screen == null || target.DeviceName != screen.DeviceName;
            opening = !was || reversing || moved || Math.Abs(offset) > .1;
            shelfSlide.Y = !was || moved ? HiddenShelfOffset : offset;
            canvas.BeginAnimation(UIElement.OpacityProperty, null); canvas.Opacity = 1;
            expanded = true;
            if (!was || moved) Position(true, target);
            if (Fullscreen() && !demo) { Collapse(false, false); return; }
            lastInside = DateTime.Now;
            if (!was || moved || renderDeferred) Render();
            if (opening)
            {
                var slide = new DoubleAnimation(shelfSlide.Y, 0, TimeSpan.FromMilliseconds(300))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                slide.Completed += delegate { if (serial == transitionSerial && !shuttingDown) opening = false; };
                shelfSlide.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        }

        void Collapse(bool manual, bool animate = true)
        {
            if (manual) { pinned = false; hideUntil = DateTime.Now.AddSeconds(1); }
            CloseImagePreview(); CloseNotePreview(); if (helpTip != null) helpTip.IsOpen = false;
            ClearPreviewCache(); SetRopeLayerCache(false);
            if (collapsing && animate) return;
            int serial = ++transitionSerial;
            opening = false;
            Action finish = delegate {
                if (serial != transitionSerial || shuttingDown) return;
                collapsing = false; expanded = false;
                Position(false, screen);
                shelfSlide.BeginAnimation(TranslateTransform.YProperty, null); shelfSlide.Y = 0;
                Render();
            };
            canvas.BeginAnimation(UIElement.OpacityProperty, null);
            canvas.Opacity = 1;
            if (!expanded || !animate) { finish(); return; }
            collapsing = true;
            var slide = new DoubleAnimation(shelfSlide.Y, HiddenShelfOffset, TimeSpan.FromMilliseconds(250))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            slide.Completed += delegate { finish(); };
            shelfSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        }
        void Toggle()
        {
            if (expanded && !collapsing) Collapse(true);
            else { pinned = true; Reveal(); }
        }
        internal void ToggleFromTray()
        {
            if (expanded && !collapsing) Collapse(true);
            else
            {
                pinned = false;
                Reveal();
                // Allow time to move from the taskbar to the shelf before auto-hiding.
                keepUntil = DateTime.Now.AddSeconds(3);
            }
        }
        internal void TogglePin()
        {
            pinned = !pinned;
            pinMenu.Checked = pinned;
            Reveal();
            if (!pinned) keepUntil = DateTime.Now.AddSeconds(3);
        }
        void Notify(string message)
        {
            toast = message.Length > 80 ? message.Substring(0, 80) : message;
            toastUntil = DateTime.Now.AddSeconds(5);
            keepUntil = toastUntil;
            Reveal(); UpdateToast();
        }
        ToolTip HelpPopup()
        {
            var content = new StackPanel { Width = 278 };
            var title = Text("拾光相册 · 使用指南", 13, "#FF27668F");
            title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, 10);
            content.Children.Add(title);
            AddHelpRow(content, "顶部蓝线", "停留展开；按住可左右调整入口");
            AddHelpRow(content, "拖动绳子 / 照片", "连续左右浏览全部照片");
            AddHelpRow(content, "屏幕左右边缘", "在照片高度停留可自动浏览");
            AddHelpRow(content, "鼠标滚轮", "快速左右浏览照片");
            AddHelpRow(content, "单击 / 双击照片", "放大预览，移开后自动收起");
            AddHelpRow(content, "大图 Ctrl + 滚轮", "缩放；放大后按住照片拖动查看");
            AddHelpRow(content, "照片上的 ×", "仅从相册移除，不删除本地照片");
            AddHelpRow(content, "右键照片", "复制、打开、备注、另存为等");
            AddHelpRow(content, "图片下方纸条", "添加或修改照片备注");
            AddHelpRow(content, "Ctrl + Alt + T", "固定展开 / 收起");
            AddHelpRow(content, "＋ 回忆便签", "记录照片背后的故事，自动保存");
            AddHelpRow(content, "双击回忆便签", "编辑内容与纸色，右键复制或删除");
            var footer = Text("托盘可导入照片 · 本地保存 · 不联网不上传", 10, "#FF81909D");
            footer.Margin = new Thickness(0, 7, 0, 0); content.Children.Add(footer);
            var body = new Border { Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(10),
                Background = Brush("#FFF9FCFF"), BorderBrush = Brush("#FFB9E2FA"), BorderThickness = new Thickness(1) };
            var tip = new ToolTip { Content = body, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), HasDropShadow = true, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                HorizontalOffset = -67, VerticalOffset = 7, FontFamily = FontFamily };
            tip.Opened += delegate { helpReading = true; };
            tip.Closed += delegate { helpReading = false; lastInside = DateTime.Now; };
            return tip;
        }
        void AddHelpRow(StackPanel content, string key, string action)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = Text(key, 10.5, "#FF347DA7"); label.TextWrapping = TextWrapping.Wrap; label.Margin = new Thickness(0, 0, 8, 0);
            var detail = new TextBlock { Text = action, FontSize = 10.5, Foreground = Brush("#FF596775"), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(detail, 1); row.Children.Add(label); row.Children.Add(detail); content.Children.Add(row);
        }
        void MakeTray()
        {
            tray = new Forms.NotifyIcon();
            try { tray.Icon = Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); }
            catch { tray.Icon = Drawing.SystemIcons.Application; }
            tray.Text = "拾光 · 桌面相册";
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("展开 / 收起（自动收起）", null, delegate { ToggleFromTray(); });
            menu.Items.Add("新增回忆便签", null, delegate { NewNote(); });
            menu.Items.Add("导入照片…", null, delegate { ChooseFiles(); });
            menu.Items.Add("入口恢复居中", null, delegate
            {
                store.Settings.EntryPositionRatio = .5;
                store.SaveSettings();
                if (!expanded) Render();
            });
            pinMenu = new Forms.ToolStripMenuItem("固定显示", null, delegate { TogglePin(); });
            menu.Items.Add(pinMenu);
            menu.Opening += delegate { pinMenu.Checked = pinned; };
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("打开相册文件夹", null, delegate { Launch(store.Inbox); });
            menu.Items.Add("清空相册…", null, delegate
            {
                dialogOpen = true;
                try
                {
                    if (Forms.MessageBox.Show("从拾光相册中移除 " + store.Items.Count + " 张照片副本？原始照片不会删除。", "拾光相册", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question) == Forms.DialogResult.Yes) store.Clear();
                }
                finally { dialogOpen = false; }
            });
            startupMenu = new Forms.ToolStripMenuItem("开机启动", null, delegate
            {
                string error;
                if (Startup != null && !Startup.Apply(!store.Settings.StartupEnabled, out error)) Notify(error);
                startupMenu.Checked = store.Settings.StartupEnabled;
            }) { Enabled = Startup != null };
            menu.Items.Add(startupMenu);
            menu.Opening += delegate { startupMenu.Checked = Startup != null && store.Settings.StartupEnabled; };
            menu.Items.Add("使用说明", null, delegate { About(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ToggleFromTray(); };
            tray.Visible = true;
        }
        void About()
        {
            dialogOpen = true;
            try
            {
                Forms.MessageBox.Show("拾光 · 桌面相册\n\n顶部蓝线：停留后展开；按住左右拖动可调整入口位置。\n拖动晾衣绳或照片：连续左右浏览全部照片。\n屏幕最左 / 最右边缘：在照片高度停留可自动浏览。\n鼠标滚轮：快速左右浏览。\n鼠标离开相册：自动收起。\n单击或双击照片：放大预览。\n大图 Ctrl + 滚轮：在 100%–300% 之间缩放。\n大图放大后：按住照片拖动，查看上下左右细节；缩回 100% 自动复位。\n鼠标离开大图：自动关闭。\n照片上的 ×：仅从相册移除，不会删除电脑里的原始照片。\n右键照片：复制、打开、添加备注、另存为或显示位置。\n图片下方纸条：添加或修改照片备注。\n托盘「导入照片」：选择一张或多张照片加入相册，最多 300 张。\n也可以把照片文件直接拖到晾衣绳上导入。\nCtrl + Alt + T：展开并固定 / 收起。\n托盘「固定显示」：保持晾衣绳展开。\n\n新导入的照片保留在原位置，相册只记录文件路径和缩略图。所有内容保存在本机，不联网、不上传。", "拾光相册", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
            }
            finally { dialogOpen = false; }
        }
        internal static bool ImageFile(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(ext);
        }

        async void Import(string file)
        {
            if (!ImageFile(file)) { Notify("支持 PNG、JPEG、BMP、GIF 和 TIFF 图片。"); return; }
            await importGate.WaitAsync();
            try
            {
                if (store.Items.Count >= Store.MaxItems) throw new InvalidOperationException("相册最多保存 300 张照片，请先移除一些照片再导入。");
                Notify("正在读取照片…");
                var prepared = await Task.Run(() => store.PrepareImport(file));
                if (shuttingDown) return;
                var added = store.CommitImport(prepared);
                Notify(added == null ? "这张照片已经在相册里。" : "照片已经挂上晾衣绳，原文件保留。");
            }
            catch (Exception ex) { store.Log("Import image", ex); Notify("无法导入：" + ex.Message); }
            finally { importGate.Release(); }
        }
        void ChooseFiles()
        {
            dialogOpen = true;
            try
            {
                using (var picker = new Forms.OpenFileDialog { Title = "导入照片到拾光相册", Multiselect = true, Filter = "照片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" })
                    if (picker.ShowDialog() == Forms.DialogResult.OK) foreach (string file in picker.FileNames) Import(file);
            }
            finally { dialogOpen = false; }
        }

        internal void Copy(Photo photo)
        {
            try
            {

                var data = new DataObject();
                var image = Store.Load(store.FullPath(photo));
                data.SetImage(image);

                SetClipboard(data);

                Notify("已复制，可直接 Ctrl + V 粘贴。");
            }
            catch (Exception ex) { store.Log("Copy image", ex); Notify("复制失败，请稍后重试。"); }

        }
        internal void CopyFile(Photo photo)
        {
            try
            {

                SetClipboard(new DataObject(DataFormats.FileDrop, new[] { store.FullPath(photo) }));

                Notify("已复制照片文件，可粘贴到文件夹。");
            }
            catch (Exception ex) { store.Log("Copy file", ex); Notify("复制文件失败，请稍后重试。"); }

        }
        internal static void SetClipboard(DataObject data)
        {
            // Clipboard owners (including chat apps) can hold the lock briefly.
            for (int attempt = 0; ; attempt++)
            {
                try { Clipboard.SetDataObject(data, true); return; }
                catch (COMException) { if (attempt >= 11) throw; Thread.Sleep(70); }
            }
        }
        void Launch(string file)
        {
            try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
            catch (Exception ex) { store.Log("Open file", ex); Notify("无法打开：" + ex.Message); }
        }
        void Edit(Photo photo)
        {
            try { Process.Start(new ProcessStartInfo("mspaint.exe", "\"" + store.FullPath(photo) + "\"") { UseShellExecute = true }); Notify("已在画图中打开，保存后晾台会更新。"); }
            catch (Exception ex) { store.Log("Open editor", ex); Notify("无法打开画图。可右键选择打开图片。"); }
        }
        void SaveAs(Photo photo)
        {
            dialogOpen = true;
            try
            {
                using (var picker = new Forms.SaveFileDialog { Title = "另存照片", FileName = Path.GetFileName(store.FullPath(photo)), Filter = "图片文件|*.*", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
                    if (picker.ShowDialog() == Forms.DialogResult.OK)
                    {
                        if (Path.GetFullPath(picker.FileName).Equals(store.FullPath(photo), StringComparison.OrdinalIgnoreCase)) return;
                        System.IO.File.Copy(store.FullPath(photo), picker.FileName, true); Notify("照片已保存，晾衣绳上的项目保留。");
                    }
            }
            catch (Exception ex) { store.Log("Save image", ex); Notify("保存失败：" + ex.Message); }
            finally { dialogOpen = false; }
        }
        MenuItem Item(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += delegate { action(); };
            return item;
        }
        ContextMenu PhotoMenu(Photo photo)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("复制图片", delegate { Copy(photo); }));
            menu.Items.Add(Item("复制照片文件", delegate { CopyFile(photo); }));
            menu.Items.Add(Item("打开图片", delegate { Launch(store.FullPath(photo)); }));
            menu.Items.Add(Item(String.IsNullOrWhiteSpace(photo.Remark) ? "添加照片备注…" : "编辑照片备注…", delegate { menu.IsOpen = false; dialogOpen = false; Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate { EditRemark(photo, null); })); }));
            menu.Items.Add(Item("另存为…", delegate { SaveAs(photo); }));
            menu.Items.Add(Item("在文件夹中显示", delegate { Process.Start("explorer.exe", "/select,\"" + store.FullPath(photo) + "\""); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("仅从相册移除（保留本地照片）", delegate { RemovePhoto(photo); }));
            menu.Opened += delegate { CloseImagePreview(); dialogOpen = true; };
            menu.Closed += delegate { dialogOpen = false; lastInside = DateTime.Now; };
            return menu;
        }
        TextBlock Text(string text, double size, string color)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center };
        }
        void Place(UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); (itemLayer ?? galleryLayer ?? canvas).Children.Add(element); }
        double EntryLeft()
        {
            return Math.Max(0, (shelfWidth - TopHoverGate.IndicatorWidth) * store.Settings.EntryPositionRatio);
        }
        double ClampRope(double value) { return Math.Max(Math.Min(0, shelfWidth - ropeWidth), Math.Min(0, value)); }
        void PanRope(double amount)
        {
            if (galleryLayer == null) return;
            ropeTarget = ClampRope(ropeTarget + amount);
            wheelPauseUntil = DateTime.Now.AddMilliseconds(260); PauseHoverEffects(true);
            lastInside = DateTime.Now;
        }
        void AnimateRope(object sender, EventArgs e)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            double elapsed = Math.Min(.05, Math.Max(0, (nowTicks - ropeFrameTicks) / (double)Stopwatch.Frequency));
            ropeFrameTicks = nowTicks;
            if (galleryLayer == null || !expanded) return;
            if (edgePanDirection != 0) ropeTarget = ClampRope(ropeTarget + edgePanDirection * 520 * elapsed);
            double distance = ropeTarget - ropeOffset;
            bool moving = ropeDragging || edgePanDirection != 0 || wheelPauseUntil != DateTime.MinValue || Math.Abs(distance) >= .15;
            SetRopeLayerCache(moving);
            if (Math.Abs(distance) < .15) ropeOffset = ropeTarget;
            else ropeOffset += distance * (1 - Math.Exp(-(ropeDragging ? 24 : 14) * elapsed));
            double pixelAligned = Math.Round(ropeOffset * 2) / 2;
            if (Math.Abs(ropeSlide.X - pixelAligned) > .01) ropeSlide.X = pixelAligned;
            RefreshVisibleItems(false);
            if (!ropeDragging && edgePanDirection == 0 && wheelPauseUntil != DateTime.MinValue && DateTime.Now >= wheelPauseUntil && Math.Abs(distance) < .5)
            { wheelPauseUntil = DateTime.MinValue; PauseHoverEffects(false); }
            if (!ropeDragging && edgePanDirection == 0 && wheelPauseUntil == DateTime.MinValue && Math.Abs(distance) < .15) SetRopeLayerCache(false);
            if (edgePanDirection != 0 || ropeDragging) lastInside = DateTime.Now;
        }
        BitmapSource CachedPreview(Photo photo, int maxWidth, int maxHeight, double aspect)
        {
            string path = store.FullPath(photo);
            long stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks;
            string key = path + "|" + stamp + "|" + maxWidth + "x" + maxHeight;
            BitmapSource cached;
            if (previewCache.TryGetValue(key, out cached)) return cached;
            cached = Store.Preview(path, maxWidth, maxHeight, aspect);
            previewCache[key] = cached; previewCacheOrder.Enqueue(key);
            while (previewCacheOrder.Count > 2) { string old = previewCacheOrder.Dequeue(); previewCache.Remove(old); }
            return cached;
        }
        void ClearPreviewCache()
        {
            previewCache.Clear(); previewCacheOrder.Clear();
        }
        void SetRopeLayerCache(bool enabled)
        {
            if (enabled && ropeDeck != null && ropeDeck.Count > 40) enabled = false;
            if (ropeLayerCached == enabled) return;
            ropeLayerCached = enabled;
            if (galleryLayer != null) galleryLayer.CacheMode = enabled ? new BitmapCache(1) : null;
        }
        void RefreshVisibleItems(bool force)
        {
            if (itemLayer == null || ropeDeck == null) return;
            // Rebuilding the item layer would detach a photo that currently owns mouse capture.
            if (!force && ropeDragging && Mouse.Captured != null && !Object.ReferenceEquals(Mouse.Captured, galleryLayer)) return;
            int count = ropeDeck.Count;
            int first = 0, last = count; // The help card occupies index count.
            if (count > 40)
            {
                double firstCenter = (ropeWidth - (count + 1) * 218) / 2 + 109;
                double viewLeft = -ropeOffset, viewRight = viewLeft + shelfWidth;
                first = Math.Max(0, (int)Math.Floor((viewLeft - firstCenter) / 218) - 3);
                last = Math.Min(count, (int)Math.Ceiling((viewRight - firstCenter) / 218) + 3);
            }
            if (!force && first == virtualStart && last == virtualEnd) return;
            CloseImagePreview(); CloseNotePreview(); if (helpTip != null) helpTip.IsOpen = false;
            ClearHoverEffects(); itemLayer.Children.Clear(); helpTip = null;
            virtualStart = first; virtualEnd = last;
            var visiblePhotos = new HashSet<Photo>();
            for (int i = first; i <= last && i < count; i++) { var visiblePhoto = ropeDeck[i] as Photo; if (visiblePhoto != null) visiblePhotos.Add(visiblePhoto); }
            store.ReleaseThumbnailsExcept(visiblePhotos);
            for (int i = first; i <= last; i++)
            {
                double center = HangingCenter(i, count);
                if (i == count) { MakeHelpCard(center, RopeY(center) + 15, i); continue; }
                var memo = ropeDeck[i] as Memo;
                if (memo != null) MakeNoteCard(memo, center, RopeY(center) + 15, i);
                else MakeCard((Photo)ropeDeck[i], center, RopeY(center) + 15, i);
            }
        }
        void StartRopeDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || galleryLayer == null) return;
            ropeDragging = true; ropeDragStartX = e.GetPosition(canvas).X; ropeDragOrigin = ropeTarget;
            PauseHoverEffects(true); galleryLayer.CaptureMouse(); CloseImagePreview(); CloseNotePreview(); e.Handled = true;
        }
        void MoveRopeDrag(object sender, MouseEventArgs e)
        {
            if (!ropeDragging || e.LeftButton != MouseButtonState.Pressed) return;
            ropeTarget = ClampRope(ropeDragOrigin + e.GetPosition(canvas).X - ropeDragStartX);
            lastInside = DateTime.Now; e.Handled = true;
        }
        void EndRopeDrag(object sender, MouseButtonEventArgs e)
        {
            if (!ropeDragging) return;
            ropeDragging = false; galleryLayer.ReleaseMouseCapture(); PauseHoverEffects(false); RefreshVisibleItems(false); lastInside = DateTime.Now; e.Handled = true;
        }
        void UpdateScreenEdgePan(Native.Point pointer)
        {
            int direction = 0;
            if (expanded && screen != null && !ropeDragging)
            {
                var area = screen.WorkingArea;
                double scale = Dpi();
                int edge = Math.Max(18, (int)Math.Ceiling(34 * scale));
                int top = area.Top + (int)Math.Floor(10 * scale);
                int bottom = area.Top + (int)Math.Ceiling(232 * scale);
                if (pointer.Y >= top && pointer.Y <= bottom)
                {
                    if (pointer.X >= area.Left && pointer.X <= area.Left + edge) direction = 1;
                    else if (pointer.X < area.Right && pointer.X >= area.Right - edge) direction = -1;
                }
            }
            if (direction == edgePanDirection) return;
            edgePanDirection = direction;
            if (direction != 0)
            {
                CloseImagePreview(); CloseNotePreview(); PauseHoverEffects(true); lastInside = DateTime.Now;
            }
            else PauseHoverEffects(false);
        }
        void AddEdgePanZones()
        {
            Action<bool> add = delegate(bool leftSide)
            {
                var arrow = new TextBlock { Text = leftSide ? "‹" : "›", FontSize = 28, Foreground = Brush("#9972C7FA"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = .28, IsHitTestVisible = false };
                var zone = new Border { Width = 64, Height = 190, Background = Brushes.Transparent, Child = arrow,
                    Cursor = Cursors.Hand, ToolTip = leftSide ? "停留向左浏览" : "停留向右浏览" };
                zone.MouseEnter += delegate { CloseImagePreview(); CloseNotePreview(); edgePanDirection = leftSide ? 1 : -1; PauseHoverEffects(true); arrow.Opacity = .85; lastInside = DateTime.Now; };
                zone.MouseLeave += delegate { edgePanDirection = 0; PauseHoverEffects(false); arrow.Opacity = .28; lastInside = DateTime.Now; };
                Canvas.SetLeft(zone, leftSide ? 0 : Math.Max(0, shelfWidth - zone.Width)); Canvas.SetTop(zone, 42); canvas.Children.Add(zone);
            };
            add(true); add(false);
        }
        Button Tool(string text, string tip, Action click)
        {
            var button = new Button { Content = text, ToolTip = tip, Height = 28, MinWidth = 28, Padding = new Thickness(8, 2, 8, 2), Background = Brush("#CCF3F5F8"), Foreground = Brush("#FF727B89"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12, Focusable = false };
            AutomationProperties.SetName(button, tip);
            button.Click += delegate { click(); };
            return button;
        }
        // Independent cards hang from a thin, gently sagging line. Empty space stays transparent.
        double HangingCenter(int index, int imageCount)
        {
            return (ropeWidth - (imageCount + 1) * 218) / 2 + (index + 0.5) * 218;
        }
        List<System.Windows.Point> RopeNodes()
        {
            var nodes = new List<System.Windows.Point>();
            nodes.Add(new System.Windows.Point(24, 18));
            int imageCount = ropeDeck == null ? Deck().Count : ropeDeck.Count, hangingCount = imageCount + 1;
            double sag = 42 + 2 * Math.Min(hangingCount, 4);
            for (int i = 0; i < hangingCount; i++)
            {
                double x = HangingCenter(i, imageCount);
                double loadPosition = (i + 1.0) / (hangingCount + 1.0);
                nodes.Add(new System.Windows.Point(x, 18 + sag * Math.Sin(Math.PI * loadPosition)));
            }
            nodes.Add(new System.Windows.Point(ropeWidth - 24, 18));
            return nodes;
        }
        static double StrandSag(System.Windows.Point a, System.Windows.Point b)
        {
            // A little slack between pegs, with the larger dip caused by the hanging photos.
            return Math.Min(8, Math.Max(1.5, (b.X - a.X) / 80));
        }
        double RopeY(double x)
        {
            var nodes = RopeNodes();
            for (int i = 1; i < nodes.Count; i++)
            {
                var a = nodes[i - 1]; var b = nodes[i];
                if (x > b.X && i < nodes.Count - 1) continue;
                double t = Math.Max(0, Math.Min(1, (x - a.X) / Math.Max(1, b.X - a.X)));
                return a.Y + (b.Y - a.Y) * t + 4 * StrandSag(a, b) * t * (1 - t);
            }
            return 18;
        }

        internal void Render(bool force = false)
        {
            if (collapsing || (remarkEditor != null && !force)) { renderDeferred = true; return; }
            renderDeferred = false;
            CloseImagePreview(); CloseNotePreview();
            if (helpTip != null) helpTip.IsOpen = false;
            edgePanDirection = 0; PauseHoverEffects(false);
            ClearHoverEffects();
            canvas.Children.Clear(); galleryLayer = null; itemLayer = null; ropeDeck = null; virtualStart = virtualEnd = -1; indicator = null; toastElement = null;
            if (!expanded) motionLayer.Children.Clear();
            if (!expanded)
            {
                var mark = new Border { Width = TopHoverGate.IndicatorWidth, Height = 8, Background = Brushes.Transparent,
                    Child = new Border { Height = TopHoverGate.IndicatorHeight, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(1), Background = Brush("#D671C6FA") },
                    Cursor = Cursors.SizeWE, ToolTip = "触碰展开 · 按住可左右拖动入口" };
                indicator = mark;
                indicatorEntered = DateTime.Now;
                mark.MouseEnter += delegate { indicatorEntered = DateTime.Now; };
                mark.MouseLeave += delegate { if (!entryDragging) indicatorEntered = DateTime.Now; };
                mark.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { entryDragging = true; mark.CaptureMouse(); e.Handled = true; };
                mark.MouseMove += delegate(object sender, MouseEventArgs e)
                {
                    if (!entryDragging || e.LeftButton != MouseButtonState.Pressed) return;
                    double markerLeft = Math.Max(0, Math.Min(shelfWidth - mark.Width, e.GetPosition(canvas).X - mark.Width / 2));
                    Canvas.SetLeft(mark, markerLeft); store.Settings.EntryPositionRatio = markerLeft / Math.Max(1, shelfWidth - mark.Width); e.Handled = true;
                };
                mark.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
                {
                    if (!entryDragging) return;
                    entryDragging = false; mark.ReleaseMouseCapture(); store.SaveSettings(); hideUntil = DateTime.Now.AddMilliseconds(300); e.Handled = true;
                };
                mark.LostMouseCapture += delegate { entryDragging = false; };
                Place(mark, EntryLeft(), 0);
                return;
            }
            ropeDeck = Deck();
            ropeWidth = Math.Max(shelfWidth, 48 + (ropeDeck.Count + 1) * 218);
            ropeOffset = ClampRope(ropeOffset);
            ropeTarget = ClampRope(ropeTarget);
            ropeLayerCached = false;
            galleryLayer = new Canvas { Width = ropeWidth, Height = ShelfHeight, Background = Brushes.Transparent, RenderTransform = ropeSlide,
                Cursor = Cursors.SizeWE };
            ropeSlide.X = ropeOffset;
            galleryLayer.MouseLeftButtonDown += StartRopeDrag;
            galleryLayer.MouseMove += MoveRopeDrag;
            galleryLayer.MouseLeftButtonUp += EndRopeDrag;
            galleryLayer.LostMouseCapture += delegate { ropeDragging = false; };
            canvas.Children.Add(galleryLayer);
            itemLayer = new Canvas { Width = ropeWidth, Height = ShelfHeight, Background = Brushes.Transparent };
            double left = 24, right = ropeWidth - 24;
            var nodes = RopeNodes();
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                drawing.BeginFigure(nodes[0], false, false);
                for (int i = 1; i < nodes.Count; i++)
                {
                    var a = nodes[i - 1]; var b = nodes[i];
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    double slack = StrandSag(a, b) * 4 / 3;
                    drawing.BezierTo(new System.Windows.Point(a.X + dx / 3, a.Y + dy / 3 + slack),
                        new System.Windows.Point(a.X + dx * 2 / 3, a.Y + dy * 2 / 3 + slack),
                        b, true, false);
                }
            }

            geometry.Freeze();
            galleryLayer.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geometry, Stroke = Brush("#8FEAF8FF"), StrokeThickness = 3.5,
                IsHitTestVisible = false, Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 1, Opacity = 0.12 }
            });
            galleryLayer.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geometry, Stroke = Brush("#FF72C7FA"), StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
            var leftKnot = new Ellipse { Width = 4, Height = 4, Fill = Brush("#FFA1DDFC"), IsHitTestVisible = false };
            Canvas.SetLeft(leftKnot, left - 2); Canvas.SetTop(leftKnot, 16); galleryLayer.Children.Add(leftKnot);
            var rightKnot = new Ellipse { Width = 4, Height = 4, Fill = Brush("#FFA1DDFC"), IsHitTestVisible = false };
            Canvas.SetLeft(rightKnot, right - 2); Canvas.SetTop(rightKnot, 16); galleryLayer.Children.Add(rightKnot);
            galleryLayer.Children.Add(itemLayer);
            RefreshVisibleItems(true);
            UpdateToast();
        }
        void UpdateToast()
        {
            if (toastElement != null) canvas.Children.Remove(toastElement);
            toastElement = null;
            if (!expanded) return;
            if (toast != "")
            {
                var note = Text(toast, 11, "#FF525F6C");
                var feedback = new Border
                {
                    Child = note, Padding = new Thickness(12, 6, 12, 6),
                    CornerRadius = new CornerRadius(12), Background = Brush("#EAF7F8FA"),
                    BorderBrush = Brush("#88D3D7DE"), BorderThickness = new Thickness(0.5),
                    IsHitTestVisible = false, Tag = "ShelfFeedback"
                };
                feedback.Measure(new System.Windows.Size(Double.PositiveInfinity, Double.PositiveInfinity));
                toastElement = feedback;
                Canvas.SetLeft(feedback, (shelfWidth - feedback.DesiredSize.Width) / 2); Canvas.SetTop(feedback, 242); canvas.Children.Add(feedback);
            }
        }

        void MakeHelpCard(double center, double y, int index)
        {
            const double width = 172, height = 148;
            double lean = 1.8;
            var swing = new RotateTransform(lean, width / 2, -15);
            var grid = new Grid { Width = width, Height = height, RenderTransform = swing, Cursor = Cursors.Hand };
            AutomationProperties.SetName(grid, "相册入口（悬停查看）");
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) };
            var title = Text("相册入口", 16, "#FF307FAF");
            title.FontWeight = FontWeights.SemiBold; title.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(title);
            var subtitle = Text("把喜欢的回忆挂起来", 11, "#FF80929F");
            subtitle.HorizontalAlignment = HorizontalAlignment.Center; subtitle.Margin = new Thickness(0, 4, 0, 0);
            content.Children.Add(subtitle);
            var importButton = Tool("＋ 导入照片", "选择一张或多张本地照片", ChooseFiles);
            importButton.FontSize = 11; importButton.Height = 26; importButton.Margin = new Thickness(0, 10, 0, 0);
            importButton.Background = Brush("#FFE4F4FF"); importButton.Foreground = Brush("#FF3C89B8");
            importButton.ToolTip = null; content.Children.Add(importButton);
            var addNote = Tool("＋ 回忆便签", "新增回忆便签", NewNote);
            addNote.FontSize = 12; addNote.Height = 27; addNote.Margin = new Thickness(0, 6, 0, 0);
            addNote.Background = Brush("#FFFFF3C9"); addNote.Foreground = Brush("#FF9D8345");
            System.Windows.Automation.AutomationProperties.SetAutomationId(addNote, "NewNote");
            content.Children.Add(addNote);
            grid.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5), Background = Brush("#F5F6FBFF"),
                BorderBrush = Brush("#E5C3E7FB"), BorderThickness = new Thickness(1),
                Child = content, Effect = new DropShadowEffect { BlurRadius = 10, Opacity = 0.16, ShadowDepth = 3 }
            });
            AddHangingPeg(grid);
            helpTip = HelpPopup();
            helpTip.PlacementTarget = grid;
            grid.ToolTip = helpTip;
            RegisterHoverPreview(grid);
            ToolTipService.SetInitialShowDelay(grid, 220);
            ToolTipService.SetShowDuration(grid, 60000);
            var breeze = new DoubleAnimation(lean - 0.6, lean + 0.6, TimeSpan.FromSeconds(3.8))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(breeze, 24);
            RegisterHangingMotion(grid, swing, breeze);
            grid.MouseEnter += delegate { if (!hoverEffectsPaused) SetHangingAnimation(swing, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))); };
            grid.MouseLeave += delegate { if (!hoverEffectsPaused) SetHangingAnimation(swing, breeze); };
            grid.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; if (HoverPreviewAllowed) helpTip.IsOpen = !helpTip.IsOpen; };
            Place(grid, center - width / 2, y);
        }
        void AddHangingPeg(Grid grid)
        {
            var pin = new Border
            {
                Width = 6, Height = 22, CornerRadius = new CornerRadius(2), Tag = "HangingPeg",
                Background = new LinearGradientBrush(Color.FromRgb(236, 238, 243), Color.FromRgb(170, 175, 190), 0),
                BorderBrush = Brush("#D8FFFFFF"), BorderThickness = new Thickness(0.5),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -20, 0, 0), IsHitTestVisible = false,
                Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 1, Opacity = 0.16 }
            };
            grid.Children.Add(pin);
            grid.Children.Add(new Border
            {
                Width = 1, Height = 14, Background = Brush("#668A909D"), Tag = "HangingPeg",
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -16, 0, 0), IsHitTestVisible = false
            });
        }

        void CloseImagePreview()
        {
            var current = imageTip;
            imageTip = null;
            if (current != null) current.IsOpen = false;
        }
        void OpenLargePreview(Photo photo, FrameworkElement sourceCard)
        {
            CloseImagePreview(); CloseNotePreview();
            if (largePreview != null) { largePreview.Close(); largePreview = null; }
            try
            {
                var thumb = store.EnsureThumbnail(photo);
                double dpi = Dpi();
                double screenWidth = screen.WorkingArea.Width / dpi;
                double screenHeight = screen.WorkingArea.Height / dpi;
                double aspect = thumb.PixelWidth / (double)Math.Max(1, thumb.PixelHeight);
                double maxImageWidth = Math.Max(520, screenWidth * .78);
                double maxImageHeight = Math.Max(360, screenHeight * .78);
                int detailWidth = Math.Min(4096, (int)Math.Ceiling(maxImageWidth * dpi * 2));
                int detailHeight = Math.Min(4096, (int)Math.Ceiling(maxImageHeight * dpi * 2));
                var full = CachedPreview(photo, detailWidth, detailHeight, aspect);

                var window = new Window
                {
                    Title = "照片预览 · " + photo.Label,
                    Width = screenWidth, Height = screenHeight,
                    Left = screen.WorkingArea.Left / dpi, Top = screen.WorkingArea.Top / dpi,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = true, Background = Brushes.Transparent,
                    ShowInTaskbar = false, Topmost = true, Owner = this
                };
                var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(178, 8, 12, 15)), Opacity = 0 };
                var dismiss = new Border { Background = Brushes.Transparent };
                root.Children.Add(dismiss);

                double imageWidth = Math.Min(maxImageWidth, maxImageHeight * aspect);
                double imageHeight = imageWidth / aspect;
                if (imageHeight > maxImageHeight) { imageHeight = maxImageHeight; imageWidth = imageHeight * aspect; }
                var picture = new Image { Source = full, Width = imageWidth, Height = imageHeight, Stretch = Stretch.Uniform };
                var photoViewport = new ScrollViewer { Content = picture, Width = imageWidth, Height = imageHeight,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = Brush("#FF11171B"), CanContentScroll = false };
                var body = new StackPanel();
                body.Children.Add(photoViewport);
                var zoomHint = new TextBlock { Text = "Ctrl + 滚轮缩放 · 放大后按住拖动 · 100%", FontSize = 11, Foreground = Brush("#FF778793"),
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 7, 0, 0) };
                body.Children.Add(zoomHint);
                double zoom = 1;
                bool panning = false;
                Border panel = null;
                DispatcherTimer leaveDelay = null;
                System.Windows.Point panStart = new System.Windows.Point();
                double panHorizontal = 0, panVertical = 0;
                photoViewport.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                    double next = Math.Max(1, Math.Min(3, zoom * (e.Delta > 0 ? 1.18 : 1 / 1.18)));
                    if (Math.Abs(next - zoom) < .001) { e.Handled = true; return; }
                    var cursor = e.GetPosition(photoViewport);
                    double relativeX = (photoViewport.HorizontalOffset + cursor.X) / Math.Max(1, photoViewport.ExtentWidth);
                    double relativeY = (photoViewport.VerticalOffset + cursor.Y) / Math.Max(1, photoViewport.ExtentHeight);
                    zoom = next; picture.Width = imageWidth * zoom; picture.Height = imageHeight * zoom;
                    photoViewport.Cursor = zoom > 1.001 ? Cursors.Hand : Cursors.Arrow;
                    zoomHint.Text = "Ctrl + 滚轮缩放 · 放大后按住拖动 · " + Math.Round(zoom * 100) + "%";
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                    {
                        if (zoom <= 1.001) { photoViewport.ScrollToHorizontalOffset(0); photoViewport.ScrollToVerticalOffset(0); }
                        else
                        {
                            photoViewport.ScrollToHorizontalOffset(relativeX * photoViewport.ExtentWidth - cursor.X);
                            photoViewport.ScrollToVerticalOffset(relativeY * photoViewport.ExtentHeight - cursor.Y);
                        }
                    }));
                    e.Handled = true;
                };
                photoViewport.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
                {
                    if (zoom <= 1.001) return;
                    panning = true; panStart = e.GetPosition(photoViewport);
                    panHorizontal = photoViewport.HorizontalOffset; panVertical = photoViewport.VerticalOffset;
                    photoViewport.Cursor = Cursors.SizeAll; photoViewport.CaptureMouse(); e.Handled = true;
                };
                photoViewport.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
                {
                    if (!panning || e.LeftButton != MouseButtonState.Pressed) return;
                    var point = e.GetPosition(photoViewport);
                    photoViewport.ScrollToHorizontalOffset(panHorizontal - (point.X - panStart.X));
                    photoViewport.ScrollToVerticalOffset(panVertical - (point.Y - panStart.Y));
                    e.Handled = true;
                };
                Action finishPan = delegate
                {
                    if (!panning) return;
                    panning = false; photoViewport.ReleaseMouseCapture();
                    photoViewport.Cursor = zoom > 1.001 ? Cursors.Hand : Cursors.Arrow;
                    if (panel != null && leaveDelay != null && !panel.IsMouseOver) leaveDelay.Start();
                };
                photoViewport.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) { finishPan(); e.Handled = true; };
                photoViewport.LostMouseCapture += delegate { panning = false; photoViewport.Cursor = zoom > 1.001 ? Cursors.Hand : Cursors.Arrow; };
                if (!String.IsNullOrWhiteSpace(photo.Remark))
                {
                    body.Children.Add(new TextBlock { Text = photo.Remark, MaxWidth = imageWidth, TextWrapping = TextWrapping.Wrap,
                        FontSize = 13, LineHeight = 21, Foreground = Brush("#FF4D555C"), Margin = new Thickness(4, 11, 4, 3) });
                }
                panel = new Border
                {
                    Child = body, Padding = new Thickness(12), CornerRadius = new CornerRadius(12),
                    Background = Brush("#FFF9FBFC"), BorderBrush = Brush("#55FFFFFF"), BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 42, ShadowDepth = 12, Opacity = .58 },
                    RenderTransformOrigin = new System.Windows.Point(.5, .5)
                };
                var scale = new ScaleTransform(.72, .72);
                panel.RenderTransform = scale;
                root.Children.Add(panel);

                var close = new Button { Content = "×", Width = 42, Height = 42, FontSize = 24, Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(115, 20, 24, 27)), BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 24, 28, 0), Cursor = Cursors.Hand, Focusable = false };
                root.Children.Add(close);
                window.Content = root;
                largePreview = window;

                leaveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                var entryGrace = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                Action hide = null;
                hide = delegate
                {
                    if (window.Tag as string == "closing") return;
                    leaveDelay.Stop();
                    window.Tag = "closing";
                    close.IsHitTestVisible = false;
                    close.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(80)));
                    panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(95)));

                    var shards = new Canvas { Width = imageWidth, Height = imageHeight,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false };
                    root.Children.Add(shards);
                    var flash = new Ellipse { Width = 54, Height = 54,
                        Fill = new RadialGradientBrush(Color.FromArgb(170, 255, 251, 224), Color.FromArgb(0, 255, 208, 92)),
                        RenderTransformOrigin = new System.Windows.Point(.5, .5), Opacity = .62 };
                    var flashScale = new ScaleTransform(.25, .25); flash.RenderTransform = flashScale;
                    Canvas.SetLeft(flash, imageWidth / 2 - 27); Canvas.SetTop(flash, imageHeight / 2 - 27); shards.Children.Add(flash);
                    var flashEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                    flashScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.25, 5.2, TimeSpan.FromMilliseconds(260)) { EasingFunction = flashEase });
                    flashScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.25, 5.2, TimeSpan.FromMilliseconds(260)) { EasingFunction = flashEase });
                    flash.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.62, 0, TimeSpan.FromMilliseconds(240)));
                    var random = new Random(photo.File == null ? 17 : photo.File.GetHashCode());
                    Color[] fireworkColors =
                    {
                        Color.FromRgb(255, 208, 76), Color.FromRgb(255, 126, 55), Color.FromRgb(255, 74, 104),
                        Color.FromRgb(245, 82, 190), Color.FromRgb(170, 103, 255), Color.FromRgb(92, 133, 255),
                        Color.FromRgb(58, 207, 255), Color.FromRgb(57, 224, 174), Color.FromRgb(146, 232, 74),
                        Color.FromRgb(255, 244, 188), Color.FromRgb(255, 255, 255)
                    };
                    var flameBrushes = new LinearGradientBrush[fireworkColors.Length];
                    var glowEffects = new DropShadowEffect[fireworkColors.Length];
                    for (int colorIndex = 0; colorIndex < fireworkColors.Length; colorIndex++)
                    {
                        Color color = fireworkColors[colorIndex];
                        var flameBrush = new LinearGradientBrush { StartPoint = new System.Windows.Point(.5, 0), EndPoint = new System.Windows.Point(.5, 1) };
                        flameBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0));
                        flameBrush.GradientStops.Add(new GradientStop(Color.FromArgb(82, color.R, color.G, color.B), .48));
                        flameBrush.GradientStops.Add(new GradientStop(Color.FromArgb(155, color.R, color.G, color.B), .8));
                        flameBrush.GradientStops.Add(new GradientStop(Color.FromArgb(195, 255, 255, 255), 1));
                        flameBrush.Freeze(); flameBrushes[colorIndex] = flameBrush;
                        var glow = new DropShadowEffect { Color = color, BlurRadius = 5, ShadowDepth = 0, Opacity = .42 };
                        glow.Freeze(); glowEffects[colorIndex] = glow;
                    }
                    int particleCount = Math.Max(56, Math.Min(88, (int)(imageWidth * imageHeight / 7600)));
                    for (int i = 0; i < particleCount; i++)
                    {
                        double sparkWidth = 2.2 + random.NextDouble() * 3.4;
                        double sparkLength = 9 + random.NextDouble() * 17;
                        double x = random.NextDouble() * Math.Max(1, imageWidth - sparkWidth);
                        double y = random.NextDouble() * Math.Max(1, imageHeight - sparkLength);
                        int particleColor = random.Next(flameBrushes.Length);
                        var shard = new Border { Width = sparkWidth, Height = sparkLength, Background = flameBrushes[particleColor],
                            CornerRadius = new CornerRadius(sparkWidth), RenderTransformOrigin = new System.Windows.Point(.5, 1), Opacity = .64 };
                        if (i % 28 == 0) shard.Effect = glowEffects[particleColor];
                        var move = new TranslateTransform();
                        var rotate = new RotateTransform();
                        var transforms = new TransformGroup(); transforms.Children.Add(rotate); transforms.Children.Add(move); shard.RenderTransform = transforms;
                        Canvas.SetLeft(shard, x); Canvas.SetTop(shard, y); shards.Children.Add(shard);

                        double centerX = x + sparkWidth / 2 - imageWidth / 2;
                        double centerY = y + sparkLength / 2 - imageHeight / 2;
                        double length = Math.Max(1, Math.Sqrt(centerX * centerX + centerY * centerY));
                        double flightAngle = Math.Atan2(centerY, centerX) * 180 / Math.PI + 90;
                        rotate.Angle = flightAngle;
                        double force = 85 + random.NextDouble() * 210;
                        double delayMs = random.Next(0, 55);
                        double durationMs = 540 + random.NextDouble() * 290;
                        var easing = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut };
                        double scatterX = (random.NextDouble() - .5) * 55;
                        double gravity = 45 + random.NextDouble() * 125;
                        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, centerX / length * force + scatterX, TimeSpan.FromMilliseconds(durationMs)) { BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = easing });
                        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, centerY / length * force + gravity, TimeSpan.FromMilliseconds(durationMs)) { BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = easing });
                        shard.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.64, 0, TimeSpan.FromMilliseconds(durationMs * .58)) { BeginTime = TimeSpan.FromMilliseconds(160 + delayMs) });
                    }
                    var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(170)) { BeginTime = TimeSpan.FromMilliseconds(790) };
                    fade.Completed += delegate { window.Close(); };
                    root.BeginAnimation(UIElement.OpacityProperty, fade);
                };
                leaveDelay.Tick += delegate { leaveDelay.Stop(); hide(); };
                entryGrace.Tick += delegate { entryGrace.Stop(); if (!panel.IsMouseOver && window.Tag == null) hide(); };
                panel.MouseEnter += delegate { entryGrace.Stop(); leaveDelay.Stop(); };
                panel.MouseLeave += delegate { if (window.Tag == null && !panning) leaveDelay.Start(); };
                dismiss.MouseLeftButtonDown += delegate { hide(); };
                close.Click += delegate { hide(); };
                window.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { hide(); e.Handled = true; } };
                window.Closed += delegate
                {
                    entryGrace.Stop(); leaveDelay.Stop(); picture.Source = null;
                    root.BeginAnimation(UIElement.OpacityProperty, null); panel.BeginAnimation(UIElement.OpacityProperty, null);
                    root.Children.Clear(); window.Content = null;
                    if (Object.ReferenceEquals(largePreview, window)) largePreview = null;
                };
                window.Show();
                window.Activate();
                if (!panel.IsMouseOver) entryGrace.Start();

                root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                var pop = new BackEase { Amplitude = .32, EasingMode = EasingMode.EaseOut };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.72, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = pop });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.72, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = pop });
            }
            catch (Exception ex) { store.Log("Large photo preview", ex); Notify("暂时无法打开大图预览。"); }
        }
        ToolTip ImagePopup(Photo photo, FrameworkElement target)
        {
            var thumb = store.EnsureThumbnail(photo);
            double dpi = Dpi();
            double maxWidth = Math.Max(100, Math.Min(760, screen.WorkingArea.Width / dpi - 64));
            double remarkBudget = String.IsNullOrWhiteSpace(photo.Remark) ? 0 : Math.Max(48, Math.Min(140, screen.WorkingArea.Height / dpi / 5));
            double maxHeight = Math.Max(80, Math.Min(460, screen.WorkingArea.Height / dpi - 300 - remarkBudget));
            double aspect = thumb.PixelWidth / (double)Math.Max(1, thumb.PixelHeight);
            double width = Math.Min(maxWidth, maxHeight * aspect), height = width / aspect;
            double bodyWidth = Math.Max(180, width);
            var preview = new Image { Width = width, Height = height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            var content = new StackPanel { Width = bodyWidth };
            ScrollViewer remarkScroll = null;
            var title = Text("放大预览", 12, "#FF387DA6");
            title.Margin = new Thickness(0, 0, 0, 8); content.Children.Add(title);
            content.Children.Add(new Border { Child = preview, Background = Brush("#FFF0F4F8"), CornerRadius = new CornerRadius(4), ClipToBounds = true });
            if (remarkBudget > 0)
            {
                var remarkText = new TextBlock { Text = photo.Remark, FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("#FF6D6042") };
                AutomationProperties.SetAutomationId(remarkText, "PreviewRemark");
                var scroll = new ScrollViewer { Content = remarkText, MaxHeight = remarkBudget, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                remarkScroll = scroll;
                content.Children.Add(new Border { Child = scroll, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 9, 0, 0),
                    CornerRadius = new CornerRadius(5), Background = Brush("#FFFFF7DD"), BorderBrush = Brush("#FFEBDDB6"), BorderThickness = new Thickness(1) });
            }
            content.Children.Add(new TextBlock { Text = "单击空白处收起 · 右键可复制或打开" + (remarkBudget > 0 ? " · 滚轮查看长备注" : ""), FontSize = 11,
                Foreground = Brush("#FF82909B"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            var tip = new ToolTip
            {
                Content = new Border { Child = content, Padding = new Thickness(10), CornerRadius = new CornerRadius(9),
                    Background = Brush("#FFF9FCFF"), BorderBrush = Brush("#FFC1E4F8"), BorderThickness = new Thickness(1) },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                HasDropShadow = true, IsHitTestVisible = false, FontFamily = FontFamily,
                Tag = remarkScroll,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, PlacementTarget = target,
                HorizontalOffset = (target.Width - bodyWidth - 22) / 2, VerticalOffset = 10
            };
            tip.Opened += delegate
            {
                if (!HoverPreviewAllowed ||
                    Mouse.LeftButton == MouseButtonState.Pressed || !store.Items.Contains(photo)) { tip.IsOpen = false; return; }
                try
                {
                    CloseImagePreview(); CloseNotePreview();
                    if (helpTip != null) helpTip.IsOpen = false;
                    preview.Source = CachedPreview(photo, (int)Math.Ceiling(maxWidth * dpi), (int)Math.Ceiling(maxHeight * dpi), aspect);
                    imageTip = tip;
                }
                catch (Exception ex) { tip.IsOpen = false; store.Log("Preview image", ex); }
            };
            tip.Closed += delegate
            {
                preview.Source = null; // Release the enlarged bitmap as soon as the pointer leaves.
                if (Object.ReferenceEquals(imageTip, tip)) imageTip = null;
                lastInside = DateTime.Now;
            };
            return tip;
        }
        void MakeCard(Photo photo, double center, double y, int index)
        {
            var thumb = store.EnsureThumbnail(photo);
            double imageWidth = Math.Max(1, thumb.PixelWidth), imageHeight = Math.Max(1, thumb.PixelHeight);
            double scale = Math.Min(192 / imageWidth, 124 / imageHeight);
            double width = Math.Max(70, imageWidth * scale) + 7;
            double height = Math.Max(54, imageHeight * scale) + 7;
            double lean = new[] { -2.2, 1.4, -1.1, 2.0, -0.6 }[index % 5];
            double cardWidth = width;
            var swing = new RotateTransform(lean, cardWidth / 2, -15);
            var card = new Grid { Width = cardWidth, Height = height + RemarkStripHeight, RenderTransform = swing, Tag = photo };
            card.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5), Background = Brush("#E8F5FF"), IsHitTestVisible = false,
                Effect = new DropShadowEffect { Color = MemoTheme.Hex("#354155"), BlurRadius = 10, Opacity = 0.18, ShadowDepth = 3 }
            });
            var grid = new Grid
            {
                Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand
            };
            AutomationProperties.SetName(grid, "悬挂图片 " + photo.Label);
            AutomationProperties.SetAutomationId(grid, "PhotoCard-" + photo.File);
            var previewTip = ImagePopup(photo, grid);
            grid.ToolTip = previewTip;
            RegisterHoverPreview(grid);
            ToolTipService.SetInitialShowDelay(grid, 300);
            ToolTipService.SetBetweenShowDelay(grid, 100);
            ToolTipService.SetShowDuration(grid, 60000);
            grid.PreviewMouseDown += delegate { CloseImagePreview(); };
            grid.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) {
                if (imageTip == null || !imageTip.IsOpen || imageTip.PlacementTarget != grid) return;
                var scroll = imageTip.Tag as ScrollViewer;
                if (scroll != null) { scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 3.0); e.Handled = true; }
            };
            var frame = new Border
            {
                CornerRadius = new CornerRadius(5, 5, 0, 0), Background = Brush("#F7F8FB"),
                BorderBrush = Brush("#C6E2F5"), BorderThickness = new Thickness(1, 1, 1, 0),
                Padding = new Thickness(2.5)
            };
            frame.Child = new Border
            {
                CornerRadius = new CornerRadius(3), ClipToBounds = true,
                Child = new Image { Source = thumb, Stretch = Stretch.Uniform }
            };
            grid.Children.Add(frame);
            AddHangingPeg(grid);
            var remove = new Button
            {
                Content = "×", Width = 19, Height = 19, FontSize = 13,
                Background = Brush("#C948505F"), Foreground = Brushes.White, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 3, 0), Focusable = false, ToolTip = "仅从相册移除，不删除本地照片",
                Opacity = 0, IsHitTestVisible = false, Tag = "DeleteButton"
            };
            AutomationProperties.SetName(remove, "移除 " + photo.Label);
            remove.PreviewMouseDown += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; RemovePhoto(photo); };
            grid.Children.Add(remove);
            var breeze = new DoubleAnimation(lean - 0.65, lean + 0.65, TimeSpan.FromSeconds(3.4 + (index % 3) * 0.6))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(breeze, 24);
            RegisterHangingMotion(card, swing, breeze);
            card.MouseEnter += delegate
            {
                if (hoverEffectsPaused) return;
                remove.IsHitTestVisible = true;
                remove.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
                SetHangingAnimation(swing, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            };
            grid.MouseLeave += delegate { CloseImagePreview(); };
            card.MouseLeave += delegate
            {
                CloseImagePreview(); CloseNotePreview();
                if (hoverEffectsPaused) return;
                remove.IsHitTestVisible = false;
                remove.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
                Timeline.SetDesiredFrameRate(breeze, 24);
                SetHangingAnimation(swing, breeze);
            };
            grid.ContextMenu = PhotoMenu(photo);
            bool down = false, moved = false, held = false;
            System.Windows.Point start = new System.Windows.Point(), startCanvas = new System.Windows.Point();
            var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            hold.Tick += delegate { hold.Stop(); if (down && !moved) { held = true; down = false; grid.ReleaseMouseCapture(); OpenLargePreview(photo, grid); } };
            grid.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (e.ClickCount == 2) { hold.Stop(); down = false; grid.ReleaseMouseCapture(); OpenLargePreview(photo, grid); return; }
                start = e.GetPosition(grid); startCanvas = e.GetPosition(canvas); down = true; moved = held = false; grid.CaptureMouse(); hold.Start();
            };
            grid.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (held || e.LeftButton != MouseButtonState.Pressed) return;
                if (moved && ropeDragging)
                {
                    ropeTarget = ClampRope(ropeDragOrigin + e.GetPosition(canvas).X - ropeDragStartX);
                    lastInside = DateTime.Now; e.Handled = true; return;
                }
                if (!down || moved) return;
                var p = e.GetPosition(grid);
                if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                moved = true; down = false; hold.Stop(); ropeDragging = true;
                ropeDragStartX = startCanvas.X; ropeDragOrigin = ropeTarget; PauseHoverEffects(true);
                ropeTarget = ClampRope(ropeDragOrigin + e.GetPosition(canvas).X - ropeDragStartX);
                CloseImagePreview(); lastInside = DateTime.Now; e.Handled = true;
            };
            grid.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                hold.Stop(); grid.ReleaseMouseCapture();
                if (down && !moved && !held) OpenLargePreview(photo, grid);
                if (ropeDragging && moved) { ropeDragging = false; PauseHoverEffects(false); RefreshVisibleItems(false); lastInside = DateTime.Now; }
                down = false; e.Handled = true;
            };
            grid.LostMouseCapture += delegate { hold.Stop(); if (ropeDragging && moved) { ropeDragging = false; PauseHoverEffects(false); } };
            card.Children.Add(grid);
            card.Children.Add(RemarkStrip(photo, cardWidth, height));
            Place(card, center - cardWidth / 2, y);
        }

        internal void Snapshot(string file)
        {
            canvas.Measure(new System.Windows.Size(shelfWidth, 270));
            canvas.Arrange(new System.Windows.Rect(0, 0, shelfWidth, 270));
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(shelfWidth), 270, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = System.IO.File.Create(file)) encoder.Save(stream);
        }
        void Cleanup(object sender, EventArgs e)
        {
            shuttingDown = true;
            CompositionTarget.Rendering -= AnimateRope;
            store.RemarkChanged = null;
            transitionSerial++; shelfSlide.BeginAnimation(TranslateTransform.YProperty, null);
            ClearHoverEffects(); CloseFallingPapers();
            ClearPreviewCache(); SetRopeLayerCache(false);
            tick.Stop();
            CloseImagePreview(); CloseNotePreview();
            if (largePreview != null) { largePreview.Close(); largePreview = null; }
            if (helpTip != null) helpTip.IsOpen = false;

            Native.UnregisterHotKey(handle, 1);
            if (source != null) source.RemoveHook(Message);
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            store.Save(); store.SaveSettings();
        }
    }

    static class Program
    {
        static string Option(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        [STAThread] static int Main(string[] args)
        {
            string root = Option(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoryGallery");
            try
            {
#if TESTS
                if (args.Contains("--self-test")) { SelfTest(root); return 0; }
                if (args.Contains("--memo-test")) return MemoTests.Run(root);
                if (args.Contains("--hover-test")) return HoverTests.Run(root);
                if (args.Contains("--startup-test")) return StartupTests.Run(root);
                if (args.Contains("--remark-test")) return RemarkTests.Run(root);
                if (args.Contains("--interaction-test")) return InteractionTests.Run(root);
#endif
                bool fresh;
                using (var mutex = new Mutex(true, "Local\\MemoryGallery-" + BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root)))).Replace("-", "").Substring(0, 20), out fresh))
                {
                    if (!fresh) { if (!args.Contains("--startup")) Native.PostMessage(new IntPtr(0xffff), Native.ShowMessage, IntPtr.Zero, IntPtr.Zero); return 0; }
                    var store = new Store(root);
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        store.Log("UI error", e.Exception);
                        e.Handled = true;
                    };
                    bool demo = args.Contains("--demo");
                    if (demo && store.Items.Count == 0)
                    {
                        store.Add(Demo("IDEAS", "把灵感挂起来", "#315B75"), "示例 · 灵感");
                        store.Add(Demo("A LITTLE BREAK", "一杯咖啡的时间", "#8C6B4D"), "示例 · 日常");
                        store.Add(Demo("READY TO SHARE", "照片，随手取用", "#426C60"), "示例 · 分享");
                    }
                    if (args.Contains("--demo-notes") && store.Notes.Count == 0)
                    {
                        store.AddNote("周末的小计划\\n去公园走走，带上相机。\\n\\n给自己留一点慢下来的时间。".Replace("\\n", "\n"), "Butter");
                        store.AddNote("今天先做这几件事\\n整理资料、回一封邮件，\\n再给新想法留个位置。".Replace("\\n", "\n"), "Mint");
                        store.AddNote("一个小灵感\\n把想到的事情写下来，\\n让它像照片一样晾着。".Replace("\\n", "\n"), "Lavender");
                    }
                    if (demo && args.Contains("--demo-remarks") && store.Items.Count > 0)
                    {
                        string[] examples = { "周五做方案时，参考这个布局。", "今天的小灵感，留给下次创作。", "这是项目需要的配色，记得和同事确认。" };
                        for (int i = 0; i < store.Items.Count; i++) store.UpdateRemark(store.Items[i], examples[i % examples.Length]);
                    }
                    var shelf = new Shelf(store, demo);
                    shelf.StartCollapsed = args.Contains("--startup");
                    if (StartupRegistration.ManagesSystemStartup(args))
                        shelf.Startup = new StartupRegistration(store, new RegistryStartupEntry(), System.Reflection.Assembly.GetExecutingAssembly().Location);
                    app.MainWindow = shelf;
                    var quit = new DispatcherTimer();
                    string seconds = Option(args, "--quit-after");
                    if (seconds != null) { quit.Interval = TimeSpan.FromSeconds(Double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)); quit.Tick += delegate { quit.Stop(); shelf.Close(); }; quit.Start(); }
                    string snapshot = Option(args, "--snapshot");
                    string diagnostics = Option(args, "--diagnostics");
                    shelf.Loaded += delegate
                    {
                        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                        delay.Tick += delegate
                        {
                            delay.Stop();
                            if (snapshot != null) shelf.Snapshot(snapshot);
                            if (diagnostics != null) System.IO.File.WriteAllText(diagnostics, "hotkey=" + shelf.HotkeyRegistered + Environment.NewLine + "items=" + store.Items.Count + Environment.NewLine + "notes=" + store.Notes.Count + Environment.NewLine + "expanded=" + shelf.Expanded + Environment.NewLine + "startupManaged=" + (shelf.Startup != null) + Environment.NewLine + "startupEnabled=" + store.Settings.StartupEnabled + Environment.NewLine + "windowHandle=" + new WindowInteropHelper(shelf).Handle.ToInt64() + Environment.NewLine);
                        };
                        delay.Start();
                    };
                    shelf.Show();
                    app.Run();
                    mutex.ReleaseMutex();
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(root);
                System.IO.File.WriteAllText(Path.Combine(root, "startup-error.log"), ex.ToString(), Encoding.UTF8);
                bool automated = args.Any(x => x.EndsWith("-test", StringComparison.Ordinal) || x == "--quit-after" || x == "--snapshot" || x == "--diagnostics");
                if (!automated) Forms.MessageBox.Show("启动失败：" + ex.Message + "\n详情已写入 " + Path.Combine(root, "startup-error.log"), "拾光相册");
                return 1;
            }
        }
        internal static BitmapSource Demo(string title, string caption, string color)
        {
            var visual = new DrawingVisual();
            using (var c = visual.RenderOpen())
            {
                var background = (SolidColorBrush)new BrushConverter().ConvertFromString(color);
                c.DrawRectangle(background, null, new System.Windows.Rect(0, 0, 640, 400));
                c.DrawEllipse(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), null, new System.Windows.Point(505, 45), 170, 170);
                c.DrawEllipse(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), null, new System.Windows.Point(60, 400), 170, 170);
                c.DrawText(new FormattedText("M E M O R Y   G A L L E R Y", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.White, 1.0), new System.Windows.Point(45, 38));
                c.DrawText(new FormattedText(title, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 35, Brushes.White, 1.0), new System.Windows.Point(45, 170));
                c.DrawText(new FormattedText(caption, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 24, Brushes.White, 1.0), new System.Windows.Point(45, 232));
                c.DrawRectangle(new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), null, new System.Windows.Rect(45, 315, 95, 3));
            }
            var bitmap = new RenderTargetBitmap(640, 400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
        }
#if TESTS
        static void Assert(bool condition, string message) { if (!condition) throw new Exception("TEST FAILED: " + message); }
        static void SelfTest(string root)
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Self-test requires an empty, isolated data directory.");
            var results = new List<string>();
            var store = new Store(root);
            BitmapSource image = Demo("TEST", "本地图片", "#426C60");
            Photo first = store.Add(image, "test");
            Assert(first != null && System.IO.File.Exists(store.FullPath(first)), "PNG saved");
            Assert(store.Add(image, "duplicate") == null && store.Items.Count == 1, "Duplicate pixels rejected");
            Assert(Store.Hash(image) == Store.Hash(Store.Load(store.FullPath(first))), "PNG round-trip hash");
            var preview = Store.Preview(store.FullPath(first), 120, 90, image.PixelWidth / (double)image.PixelHeight);
            Assert(preview.PixelWidth <= 120 && preview.PixelHeight <= 90 && preview.IsFrozen, "Preview decode is bounded and frozen");
            using (var exclusive = System.IO.File.Open(store.FullPath(first), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert(exclusive.Length > 0, "Preview releases its source file handle");
            Assert(Store.Hash(image) == Store.Hash(Store.Load(store.FullPath(first))), "Preview preserves cache image");
            results.Add("PASS: enlarged preview decoding is bounded, source handle released and cached image unchanged");
            var restored = new Store(root);
            Assert(restored.Items.Count == 1 && restored.Items[0].Thumb != null, "Persistence and thumbnails");
            string external = Path.Combine(root, "external.png");
            System.IO.File.Copy(store.FullPath(first), external);
            store.Remove(first);
            Assert(System.IO.File.Exists(external), "Remove preserves external original");
            Assert(store.Items.Count == 0 && !System.IO.File.Exists(store.FullPath(first)), "Remove clears only cache");
            Photo imported = store.Import(external);
            Assert(imported != null && imported.IsReference && store.FullPath(imported) == Path.GetFullPath(external), "Import keeps a safe reference to the original");
            Assert(!store.IsSafe("../external.png") && !store.IsSafe("external.png") && !store.IsSafe("C:\\x.png"), "Cache path traversal blocked");
            store.Clear();
            Assert(System.IO.File.Exists(external), "Clear preserves source");
            store.Settings.EntryPositionRatio = .25; store.SaveSettings();
            Assert(Math.Abs(new Store(root).Settings.EntryPositionRatio - .25) < .001, "Preference persistence");
            Assert(Shelf.ImageFile("x.PNG") && !Shelf.ImageFile("x.exe"), "Image extension filter");
            for (int i = store.Items.Count; i < Store.MaxItems; i++)
                store.Add(Demo("photo " + i, "测试", i % 2 == 0 ? "#426C60" : "#315B75"), "item " + i);
            bool limitRejected = false;
            try { store.Add(Demo("OVER LIMIT", "测试", "#426C60"), "over limit"); }
            catch (InvalidOperationException) { limitRejected = true; }
            Assert(store.Items.Count == Store.MaxItems && limitRejected, "300-photo limit rejects additions");
            Assert(Directory.GetFiles(store.Inbox, "*.png").Length == Store.MaxItems, "Rejected addition does not create a file");
            Assert(new Store(root).Items.Count == Store.MaxItems, "Retention survives restart");
            results.Add("PASS: PNG save/load, pixel deduplication, persistence, thumbnails");
            results.Add("PASS: imported source preserved during remove and clear");
            results.Add("PASS: path traversal rejected, settings persisted, extension filtering");
            results.Add("PASS: 300-photo hard limit preserves existing items");
            System.IO.File.WriteAllLines(Path.Combine(root, "self-test-results.txt"), results, Encoding.UTF8);
        }
#endif
    }
}
