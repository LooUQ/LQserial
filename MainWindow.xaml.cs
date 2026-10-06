using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LQserial;

public partial class MainWindow : Window
{
    const int CommandCount = 29;
    const int MaxRxChars = 500_000;

    readonly List<CommandRow> _rows = Enumerable.Range(1, CommandCount).Select(i => new CommandRow { Index = i }).ToList();
    readonly Decoder _decoder = Encoding.Latin1.GetDecoder();
    SerialPort? _port;
    CancellationTokenSource? _runCts;
    bool _atLineStart = true;
    bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();

        RefreshPorts();
        if (PortBox.Items.Count > 0) PortBox.SelectedIndex = 0;
        foreach (var b in new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 }) BaudBox.Items.Add(b);
        BaudBox.Text = "115200";
        foreach (var s in new[] { "1", "1.5", "2" }) StopBox.Items.Add(s);
        StopBox.SelectedIndex = 0;
        foreach (var p in new[] { "None", "Odd", "Even", "Mark", "Space" }) ParityBox.Items.Add(p);
        ParityBox.SelectedIndex = 0;
        foreach (var d in new[] { 5, 6, 7, 8 }) DataBox.Items.Add(d);
        DataBox.SelectedItem = 8;
        foreach (var f in new[] { "No Ctrl Flow", "RTS/CTS", "XON/XOFF" }) FlowBox.Items.Add(f);
        FlowBox.SelectedIndex = 0;

        CmdList.ItemsSource = _rows;
        LogPathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"COM_LOG-{DateTime.Now:yyyyMMdd}.txt");
        _loading = false;
        UpdateUiState();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.F1) { ShowHelp(); e.Handled = true; } };

        try
        {
            if (File.Exists(LastScriptFile) && File.ReadAllText(LastScriptFile).Trim() is { Length: > 0 } last && File.Exists(last))
                LoadScript(last);
        }
        catch { }
    }

    // ---- Help ----

    HelpWindow? _help;

    void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();

    void ShowHelp()
    {
        if (_help is { IsLoaded: true }) { _help.Activate(); return; }
        _help = new HelpWindow { Owner = this };
        _help.Show();
    }

    void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    // ---- Title bar color (Windows 11) ----

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // COLORREF is 0x00BBGGRR
        int caption = 0x7A3A01;   // #013A7A
        int text = 0xFFFFFF;
        int border = 0x9C4A01;    // #014A9C
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));  // DWMWA_CAPTION_COLOR
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));     // DWMWA_TEXT_COLOR
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));   // DWMWA_BORDER_COLOR
    }

    // ---- Port handling ----

    void RefreshPorts()
    {
        var current = (PortBox.SelectedItem as PortInfo)?.Name;
        PortBox.Items.Clear();
        foreach (var p in PortInfo.Enumerate()) PortBox.Items.Add(p);
        if (current != null)
            PortBox.SelectedItem = PortBox.Items.Cast<PortInfo>().FirstOrDefault(p => p.Name == current);
    }

    void PortBox_DropDownOpened(object? sender, EventArgs e) { if (_port is not { IsOpen: true }) RefreshPorts(); }

    void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_port is { IsOpen: true }) { ClosePort(); return; }

        if (PortBox.SelectedItem is not PortInfo selected) { MessageBox.Show("Select a COM port."); return; }
        var name = selected.Name;
        if (!int.TryParse(BaudBox.Text, out var baud)) { MessageBox.Show("Invalid baud rate."); return; }

        try
        {
            var port = new SerialPort(name, baud)
            {
                DataBits = (int)DataBox.SelectedItem,
                StopBits = StopBox.SelectedIndex switch { 1 => StopBits.OnePointFive, 2 => StopBits.Two, _ => StopBits.One },
                Parity = (Parity)ParityBox.SelectedIndex,
                Handshake = FlowBox.SelectedIndex switch { 1 => Handshake.RequestToSend, 2 => Handshake.XOnXOff, _ => Handshake.None },
                ReadTimeout = 500,
                WriteTimeout = 2000,
            };
            port.DataReceived += Port_DataReceived;
            port.Open();
            if (port.Handshake != Handshake.RequestToSend) port.RtsEnable = RtsChk.IsChecked == true;
            port.DtrEnable = DtrChk.IsChecked == true;
            _port = port;
            LogEvent($"{name} opened ({baud}, {port.DataBits}, {port.Parity}, {port.StopBits}, {port.Handshake})");
            if (selected.Details.Length > 0) LogEvent($"{name}: {selected.Details}");
        }
        catch (Exception ex)
        {
            LogEvent($"Cannot open {name}: {ex.Message}");
            MessageBox.Show(ex.Message, "Cannot open port", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateUiState();
    }

    void ClosePort()
    {
        _runCts?.Cancel();
        var port = _port;
        _port = null;
        if (port != null)
        {
            LogEvent($"{port.PortName} closed");
            port.DataReceived -= Port_DataReceived;
            try { port.Close(); } catch { }
            port.Dispose();
        }
        UpdateUiState();
    }

    void UpdateUiState()
    {
        var open = _port is { IsOpen: true };
        OpenBtn.Content = open ? "Close Port" : "Open Port";
        foreach (var c in new Control[] { PortBox, BaudBox, StopBox, ParityBox, DataBox, FlowBox }) c.IsEnabled = !open;
        SendCmdBtn.IsEnabled = SendFileBtn.IsEnabled = open;
    }

    void Dtr_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading && _port is { IsOpen: true } p) p.DtrEnable = DtrChk.IsChecked == true;
    }

    void Rts_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading && _port is { IsOpen: true } p && p.Handshake != Handshake.RequestToSend) p.RtsEnable = RtsChk.IsChecked == true;
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => ClosePort();

    // ---- Receive ----

    void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var port = (SerialPort)sender;
        byte[] buf;
        try
        {
            var n = port.BytesToRead;
            if (n <= 0) return;
            buf = new byte[n];
            n = port.Read(buf, 0, n);
            if (n < buf.Length) Array.Resize(ref buf, n);
        }
        catch { return; }

        Dispatcher.BeginInvoke(() => AppendRx(buf));
    }

    void AppendRx(byte[] data)
    {
        string text;
        if (ShowHexChk.IsChecked == true)
            text = string.Join(' ', data.Select(b => b.ToString("X2"))) + " ";
        else
        {
            var chars = new char[_decoder.GetCharCount(data, 0, data.Length)];
            _decoder.GetChars(data, 0, data.Length, chars, 0);
            text = new string(chars).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        if (ShowTimeChk.IsChecked == true)
        {
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                if (_atLineStart && ch != '\n') { sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] "); _atLineStart = false; }
                sb.Append(ch);
                if (ch == '\n') _atLineStart = true;
            }
            text = sb.ToString();
        }
        else _atLineStart = text.EndsWith('\n');

        AppendMain(text, received: true);
    }

    static readonly System.Windows.Media.Brush RxBrush = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x00, 0x00)));

    static System.Windows.Media.Brush Freeze(System.Windows.Media.Brush b) { b.Freeze(); return b; }

    int _rxChars;

    void AppendMain(string text, bool received = false)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) { RxPara.Inlines.Add(new System.Windows.Documents.LineBreak()); _rxChars++; }
            if (lines[i].Length == 0) continue;
            var run = new System.Windows.Documents.Run(lines[i]);
            if (received) { run.Foreground = RxBrush; run.FontWeight = FontWeights.Bold; }
            RxPara.Inlines.Add(run);
            _rxChars += lines[i].Length;
        }

        // keep the document bounded: drop the oldest inlines once past the limit
        if (_rxChars > MaxRxChars)
            while (_rxChars > MaxRxChars / 2 && RxPara.Inlines.FirstInline is { } first)
            {
                _rxChars -= first is System.Windows.Documents.Run r ? r.Text.Length : 1;
                RxPara.Inlines.Remove(first);
            }
        RxBox.ScrollToEnd();

        if (SaveLogChk.IsChecked == true)
        {
            try { File.AppendAllText(LogPathBox.Text, text, Encoding.UTF8); }
            catch { SaveLogChk.IsChecked = false; LogEvent("Log write failed; logging disabled"); }
        }
    }

    void LogEvent(string message)
    {
        EventBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        EventBox.ScrollToEnd();
    }

    // ---- Send ----

    static byte[]? ParseHex(string s)
    {
        var tokens = s.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(t => t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t).ToList();
        var bytes = new List<byte>();
        foreach (var t in tokens)
        {
            var tok = t.Length % 2 == 1 ? "0" + t : t;
            for (var i = 0; i < tok.Length; i += 2)
            {
                if (!byte.TryParse(tok.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return null;
                bytes.Add(b);
            }
        }
        return bytes.ToArray();
    }

    bool Send(string text, bool hex, bool enter)
    {
        if (_port is not { IsOpen: true } port) { MessageBox.Show("Port is not open."); return false; }

        byte[] data;
        if (hex)
        {
            var parsed = ParseHex(text);
            if (parsed == null) { MessageBox.Show($"Invalid HEX string: {text}"); return false; }
            data = parsed;
        }
        else data = Encoding.Latin1.GetBytes(text);
        if (enter) data = data.Concat(new byte[] { 0x0D, 0x0A }).ToArray();

        try { port.Write(data, 0, data.Length); }
        catch (Exception ex) { LogEvent($"Write failed: {ex.Message}"); MessageBox.Show(ex.Message, "Write failed"); return false; }

        var shown = hex ? BitConverter.ToString(data).Replace('-', ' ') : text;
        AppendMain((_atLineStart ? "" : "\n") + (ShowTimeChk.IsChecked == true ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "") + $"TX: {shown}\n");
        _atLineStart = true;
        return true;
    }

    void SendCmd_Click(object sender, RoutedEventArgs e) =>
        Send(InputBox.Text, HexStringChk.IsChecked == true, SendEnterChk.IsChecked == true);

    void RowSend_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is CommandRow r) Send(r.Text, r.Hex, r.Enter);
    }

    void ClearInfo_Click(object sender, RoutedEventArgs e)
    {
        RxPara.Inlines.Clear();
        _rxChars = 0;
        EventBox.Clear();
        _atLineStart = true;
    }

    // ---- File send / log ----

    void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog();
        if (dlg.ShowDialog() == true) FilePathBox.Text = dlg.FileName;
    }

    async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        if (_port is not { IsOpen: true } port || !File.Exists(FilePathBox.Text)) return;
        SendFileBtn.IsEnabled = false;
        try
        {
            var data = await File.ReadAllBytesAsync(FilePathBox.Text);
            await Task.Run(() =>
            {
                for (var i = 0; i < data.Length; i += 256)
                    port.Write(data, i, Math.Min(256, data.Length - i));
            });
            LogEvent($"File sent: {Path.GetFileName(FilePathBox.Text)} ({data.Length} bytes)");
        }
        catch (Exception ex) { LogEvent($"Send file failed: {ex.Message}"); MessageBox.Show(ex.Message, "Send file failed"); }
        finally { SendFileBtn.IsEnabled = _port is { IsOpen: true }; }
    }

    void SaveLogPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = Path.GetFileName(LogPathBox.Text), Filter = "Text|*.txt|All|*.*" };
        if (dlg.ShowDialog() == true) LogPathBox.Text = dlg.FileName;
    }

    void SaveLog_Changed(object sender, RoutedEventArgs e) { }

    // ---- Command list / scripts ----

    void AllChk_Changed(object sender, RoutedEventArgs e)
    {
        var on = AllChk.IsChecked == true;
        foreach (var r in _rows) r.Selected = on;
    }

    void CmdScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // Wheel over a command field whose text is wider than the box scrolls the text sideways
        for (var d = e.OriginalSource as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is TextBox tb)
            {
                if (tb.ExtentWidth > tb.ViewportWidth)
                {
                    tb.ScrollToHorizontalOffset(tb.HorizontalOffset - e.Delta / 2.0);
                    e.Handled = true;
                    return;
                }
                break;
            }
        }

        // Otherwise scroll the list (text boxes would swallow the wheel otherwise)
        CmdScroll.ScrollToVerticalOffset(CmdScroll.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    void ClearCmds_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) { r.Text = ""; r.Delay = ""; r.Hex = false; r.Enter = true; r.Selected = false; }
        AllChk.IsChecked = false;
    }

    static readonly string LastScriptFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LQserial", "lastscript.txt");

    void SaveScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Script (*.ini)|*.ini", DefaultExt = ".ini" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("[SET]");
            sb.AppendLine($"RunTimes={RunTimesBox.Text}");
            sb.AppendLine($"DelayTime={DelayBox.Text}");
            foreach (var r in _rows)
            {
                sb.AppendLine($"[{r.Index}]");
                sb.AppendLine($"Cho={(r.Selected ? 1 : 0)}");
                sb.AppendLine($"CMD={r.Text}");
                sb.AppendLine($"Delay={r.Delay}");
                sb.AppendLine($"HEX={(r.Hex ? 1 : 0)}");
                sb.AppendLine($"Enter={(r.Enter ? 1 : 0)}");
            }
            File.WriteAllText(dlg.FileName, sb.ToString());
            LogEvent($"Script saved: {dlg.FileName}");
        }
        catch (Exception ex) { LogEvent($"Save script failed: {ex.Message}"); }
    }

    void LoadScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Script (*.ini)|*.ini|All|*.*" };
        if (dlg.ShowDialog() == true) LoadScript(dlg.FileName);
    }

    void LoadScript(string path)
    {
        try
        {
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? cur = null;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[' && line[^1] == ']') { cur = sections[line[1..^1]] = new(StringComparer.OrdinalIgnoreCase); continue; }
                var eq = line.IndexOf('=');
                if (eq > 0 && cur != null) cur[line[..eq].Trim()] = raw[(raw.IndexOf('=') + 1)..].TrimEnd();
            }

            if (sections.TryGetValue("SET", out var set))
            {
                if (set.TryGetValue("RunTimes", out var rt)) RunTimesBox.Text = rt;
                if (set.TryGetValue("DelayTime", out var dt)) DelayBox.Text = dt;
            }
            foreach (var r in _rows)
            {
                sections.TryGetValue(r.Index.ToString(), out var sec);
                string Get(string k) => sec != null && sec.TryGetValue(k, out var v) ? v : "";
                r.Selected = Get("Cho") == "1";
                r.Text = Get("CMD");
                r.Delay = Get("Delay");
                r.Hex = Get("HEX") == "1";
                r.Enter = Get("Enter") != "0";
            }
            AllChk.IsChecked = false;
            LogEvent($"Script loaded: {path}");

            Directory.CreateDirectory(Path.GetDirectoryName(LastScriptFile)!);
            File.WriteAllText(LastScriptFile, path);
        }
        catch (Exception ex) { LogEvent($"Load script failed: {ex.Message}"); }
    }

    async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_port is not { IsOpen: true }) { MessageBox.Show("Port is not open."); return; }
        var selected = _rows.Where(r => r.Selected && !string.IsNullOrEmpty(r.Text)).ToList();
        if (selected.Count == 0) { MessageBox.Show("No commands selected."); return; }
        _ = int.TryParse(RunTimesBox.Text, out var times);
        _ = int.TryParse(DelayBox.Text, out var defaultDelay);
        times = Math.Max(1, times);

        LogEvent($"Run started: {selected.Count} command(s) x {times}");
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        RunBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        try
        {
            for (var n = 0; n < times; n++)
                foreach (var r in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Send(r.Text, r.Hex, r.Enter)) return;
                    var delay = int.TryParse(r.Delay, out var d) && d > 0 ? d : defaultDelay;
                    await Task.Delay(delay, ct);
                }
        }
        catch (OperationCanceledException) { }
        finally
        {
            LogEvent(ct.IsCancellationRequested ? "Run stopped" : "Run finished");
            RunBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
        }
    }

    void Stop_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();
}
