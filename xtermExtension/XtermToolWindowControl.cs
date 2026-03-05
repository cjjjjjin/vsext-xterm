using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace xtermExtension
{
    public class XtermToolWindowControl : UserControl
    {
        private const int FlushIntervalMs = 16;
        private const int MaxBatchSize = 8192;

        private readonly object pendingLock = new object();
        private readonly List<string> pendingWrites = new List<string>();
        private readonly object batchLock = new object();
        private readonly StringBuilder writeBatch = new StringBuilder();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly SemaphoreSlim inputWriteGate = new SemaphoreSlim(1, 1);

        private readonly WebView2 terminalWebView;
        private readonly Button resetButton;

        private ConPtySession conPtySession;
        private Stream inputStream;
        private CancellationTokenSource streamCts;
        private Task stdoutPumpTask;
        private System.Threading.Timer flushTimer;

        private volatile bool isTerminalReady;
        private int flushTimerRunning;
        private int terminalCols = 120;
        private int terminalRows = 30;
        private bool isDisposing;
        private bool isResetting;
        private bool isWebMessageHooked;
        public bool IsClosed { get; private set; }

        public XtermToolWindowControl()
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "xtermExtension",
                "WebView2");

            Directory.CreateDirectory(userDataFolder);

            terminalWebView = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = userDataFolder
                }
            };

            resetButton = new Button
            {
                Content = "Reset",
                Width = 90,
                Height = 28,
                Margin = new Thickness(8)
            };
            resetButton.Click += OnResetClicked;

            DockPanel root = new DockPanel();
            DockPanel.SetDock(resetButton, Dock.Bottom);
            root.Children.Add(resetButton);
            root.Children.Add(terminalWebView);

            Content = root;
            Loaded += OnLoaded;
            IsVisibleChanged += OnIsVisibleChanged;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;

            try
            {
                await terminalWebView.EnsureCoreWebView2Async();
                terminalWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                terminalWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                isWebMessageHooked = true;
                terminalWebView.NavigateToString(BuildTerminalHtml());
                StartConPtyBackend();
            }
            catch (Exception ex)
            {
                Content = new TextBlock
                {
                    Margin = new Thickness(12),
                    Text = "WebView2/terminal initialization failed: " + ex.Message,
                    TextWrapping = TextWrapping.Wrap
                };
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            LogEvent("Unloaded fired");

            if (isDisposing)
            {
                LogEvent("Unloaded during close");
                return;
            }

            LogEvent("Unloaded by tab switch/layout change (no cleanup)");
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            bool isVisible = (bool)e.NewValue;
            LogEvent("IsVisibleChanged fired: IsVisible=" + isVisible);

            if (isVisible && !isDisposing)
            {
                try
                {
                    if (IsClosed)
                    {
                        _ = ReviveAfterCloseAsync();
                    }

                    terminalWebView.Focus();
                    SendMessageToTerminal("fit", null);
                    SendMessageToTerminal("focus", null);
                    LogEvent("Visibility restore: focus+fit posted");
                }
                catch (Exception ex)
                {
                    LogEvent("Visibility restore failed: " + ex.Message);
                }
            }
        }

        private static void LogEvent(string message)
        {
            string line = "[xtermExtension] " + DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
            Console.WriteLine(line);
            Debug.WriteLine(line);
        }

        public void BeginClose()
        {
            if (isDisposing)
            {
                LogEvent("BeginClose skipped because dispose is already in progress");
                return;
            }

            isDisposing = true;
            IsClosed = true;
            LogEvent("BeginClose started");

            if (terminalWebView?.CoreWebView2 != null)
            {
                terminalWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                isWebMessageHooked = false;
            }

            _ = CleanupForCloseAsync();
        }

        private async Task CleanupForCloseAsync()
        {
            await StopConPtyBackendAsync();

            try { flushTimer?.Dispose(); } catch { }

            LogEvent("BeginClose cleanup completed");
        }

        private async Task ReviveAfterCloseAsync()
        {
            if (!IsClosed)
            {
                return;
            }

            LogEvent("ReviveAfterClose started");

            try
            {
                if (terminalWebView.CoreWebView2 == null)
                {
                    await terminalWebView.EnsureCoreWebView2Async();
                    terminalWebView.NavigateToString(BuildTerminalHtml());
                }

                if (!isWebMessageHooked && terminalWebView.CoreWebView2 != null)
                {
                    terminalWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                    isWebMessageHooked = true;
                }

                isDisposing = false;
                IsClosed = false;
                isTerminalReady = false;

                if (conPtySession == null)
                {
                    StartConPtyBackend();
                }

                SendMessageToTerminal("clear", null);
                SendMessageToTerminal("fit", null);
                SendMessageToTerminal("focus", null);
                LogEvent("ReviveAfterClose completed");
            }
            catch (Exception ex)
            {
                LogEvent("ReviveAfterClose failed: " + ex.Message);
            }
        }

        public void EnsureActiveAfterShow()
        {
            if (IsClosed)
            {
                _ = ReviveAfterCloseAsync();
            }
        }

        private async void OnResetClicked(object sender, RoutedEventArgs e)
        {
            if (isDisposing || isResetting)
            {
                return;
            }

            isResetting = true;
            LogEvent("Reset clicked");

            try
            {
                resetButton.IsEnabled = false;
                await StopConPtyBackendAsync();
                lock (pendingLock)
                {
                    pendingWrites.Clear();
                }
                lock (batchLock)
                {
                    writeBatch.Clear();
                }
                isTerminalReady = false;
                terminalWebView.NavigateToString(BuildTerminalHtml());
                StartConPtyBackend();
                LogEvent("Reset completed");
            }
            finally
            {
                resetButton.IsEnabled = true;
                isResetting = false;
            }
        }

        private void StartConPtyBackend()
        {
            if (!ConPtySession.IsSupported())
            {
                QueueWrite("ConPTY is not supported on this OS.\r\n");
                return;
            }

            try
            {
                string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                conPtySession = new ConPtySession(cmdPath, "/Q", Environment.CurrentDirectory, terminalCols, terminalRows);
                conPtySession.ProcessExited += (_, __) => QueueWrite("ConPTY process exited.\r\n");

                streamCts = new CancellationTokenSource();
                stdoutPumpTask = Task.Run(() => PumpStreamLoop(conPtySession.OutputStream, streamCts.Token));
                inputStream = conPtySession.InputStream;

                QueueWrite("ConPTY backend active.\r\n");
                _ = SendInputAsync("chcp 65001>nul\r");
            }
            catch (Exception ex)
            {
                QueueWrite("ConPTY init failed: " + ex.Message + "\r\n");
                try { conPtySession?.Dispose(); } catch { }
                conPtySession = null;
                inputStream = null;
            }
        }

        private async Task StopConPtyBackendAsync()
        {
            inputStream = null;

            ConPtySession session = conPtySession;
            conPtySession = null;

            if (session != null)
            {
                try { session.Kill(); } catch { }
                try { session.Dispose(); } catch { }
            }

            CancellationTokenSource cts = streamCts;
            streamCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }

            Task pump = stdoutPumpTask;
            stdoutPumpTask = null;
            if (pump != null)
            {
                try { await pump; } catch { }
            }

            try { cts?.Dispose(); } catch { }
        }

        private void PumpStreamLoop(Stream source, CancellationToken ct)
        {
            Utf8DecoderHelper decoder = new Utf8DecoderHelper();
            byte[] buffer = new byte[4096];

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    string text = decoder.Decode(buffer, 0, read);
                    if (!string.IsNullOrEmpty(text))
                    {
                        QueueWrite(text);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                decoder.Dispose();
            }
        }

        private void QueueWrite(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            lock (pendingLock)
            {
                if (!isTerminalReady)
                {
                    pendingWrites.Add(data);
                    return;
                }
            }

            lock (batchLock)
            {
                writeBatch.Append(data);
                bool flushNow = writeBatch.Length >= MaxBatchSize;

                if (Interlocked.CompareExchange(ref flushTimerRunning, 1, 0) == 0)
                {
                    flushTimer?.Dispose();
                    flushTimer = new System.Threading.Timer(
                        FlushWriteBatch,
                        null,
                        flushNow ? 0 : FlushIntervalMs,
                        Timeout.Infinite);
                }
                else if (flushNow)
                {
                    flushTimer?.Change(0, Timeout.Infinite);
                }
            }
        }

        private void FlushPendingWrites()
        {
            string pending = null;

            lock (pendingLock)
            {
                if (pendingWrites.Count == 0)
                {
                    return;
                }

                pending = string.Concat(pendingWrites);
                pendingWrites.Clear();
            }

            QueueWrite(pending);
        }

        private void FlushWriteBatch(object state)
        {
            string batch = null;

            lock (batchLock)
            {
                if (writeBatch.Length > 0)
                {
                    batch = writeBatch.ToString();
                    writeBatch.Clear();
                }

                Interlocked.Exchange(ref flushTimerRunning, 0);
            }

            if (string.IsNullOrEmpty(batch) || isDisposing)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(() => SendMessageToTerminal("write", batch));
        }

        private async void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (isDisposing)
            {
                return;
            }

            TerminalMessage message;
            try
            {
                message = json.Deserialize<TerminalMessage>(e.WebMessageAsJson);
            }
            catch
            {
                return;
            }

            if (message == null || string.IsNullOrEmpty(message.type))
            {
                return;
            }

            switch (message.type)
            {
                case "ready":
                    lock (pendingLock)
                    {
                        isTerminalReady = true;
                    }
                    LogEvent("Terminal ready message received");
                    FlushPendingWrites();
                    SendMessageToTerminal("fit", null);
                    SendMessageToTerminal("focus", null);
                    break;

                case "input":
                    if (!isTerminalReady)
                    {
                        lock (pendingLock)
                        {
                            isTerminalReady = true;
                        }
                        LogEvent("Terminal ready fallback activated by input");
                        FlushPendingWrites();
                    }

                    if (!string.IsNullOrEmpty(message.data))
                    {
                        LogEvent("Input message received: len=" + message.data.Length);
                        await SendInputAsync(message.data);
                    }
                    break;

                case "resized":
                    if (message.cols > 0 && message.rows > 0)
                    {
                        terminalCols = message.cols;
                        terminalRows = message.rows;

                        if (conPtySession != null)
                        {
                            try
                            {
                                conPtySession.Resize(terminalCols, terminalRows);
                            }
                            catch (Exception ex)
                            {
                                QueueWrite("Resize failed: " + ex.Message + "\r\n");
                            }
                        }
                    }
                    break;
            }
        }

        private async Task SendInputAsync(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return;
            }

            try
            {
                await inputWriteGate.WaitAsync();
                try
                {
                    Stream stream = inputStream;
                    if (stream == null)
                    {
                        LogEvent("SendInputAsync skipped: inputStream is null");
                        return;
                    }

                    byte[] bytes = Encoding.UTF8.GetBytes(input);
                    await Task.Run(() =>
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    });
                    LogEvent("SendInputAsync wrote bytes: " + bytes.Length);
                }
                finally
                {
                    inputWriteGate.Release();
                }
            }
            catch (Exception ex)
            {
                LogEvent("SendInputAsync failed: " + ex.Message);
            }
        }

        private void SendMessageToTerminal(string type, string data)
        {
            if (isDisposing || terminalWebView?.CoreWebView2 == null)
            {
                return;
            }

            Dictionary<string, object> msg = new Dictionary<string, object>
            {
                { "type", type }
            };

            if (!string.IsNullOrEmpty(data))
            {
                msg["data"] = data;
            }

            terminalWebView.CoreWebView2.PostWebMessageAsJson(json.Serialize(msg));
        }

        private static string BuildTerminalHtml()
        {
            return @"<!DOCTYPE html>
<html lang='en'>
<head>
  <meta charset='UTF-8' />
  <meta name='viewport' content='width=device-width, initial-scale=1.0' />
  <link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.css' />
  <style>
    * { margin: 0; padding: 0; box-sizing: border-box; }
    html, body { width: 100%; height: 100%; overflow: hidden; background-color: #1e1e1e; }
    #terminal-container { width: 100%; height: 100%; overflow: hidden; background-color: #1e1e1e; }
    .xterm-viewport::-webkit-scrollbar { width: 0; height: 0; }
    .xterm-viewport { scrollbar-width: none; -ms-overflow-style: none; }
    .xterm { width: 100%; height: 100%; }
  </style>
</head>
<body>
  <div id='terminal-container'></div>
  <script src='https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.js'></script>
  <script src='https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.js'></script>
  <script>
    (function() {
      'use strict';
      let terminal = null;
      let fitAddon = null;

      function postMessageToHost(message) {
        if (window.chrome && window.chrome.webview) {
          window.chrome.webview.postMessage(message);
        }
      }

      function setupMessageListener() {
        if (!(window.chrome && window.chrome.webview)) {
          return;
        }

        window.chrome.webview.addEventListener('message', function(event) {
          const message = event.data;
          if (!message || !message.type) {
            return;
          }

            switch (message.type) {
            case 'write':
              if (terminal && message.data) {
                terminal.write(message.data);
              }
              break;
            case 'clear':
              if (terminal) {
                terminal.clear();
              }
              break;
            case 'fit':
              if (fitAddon) {
                fitAddon.fit();
              }
              break;
            case 'focus':
              if (terminal) {
                terminal.focus();
                const textarea = document.querySelector('.xterm-helper-textarea');
                if (textarea) {
                  textarea.focus();
                }
              }
              break;
          }
        });
      }

      function init() {
        if (typeof Terminal === 'undefined' || typeof FitAddon === 'undefined') {
          document.body.innerHTML = '<p style=""color:#ff6b6b;padding:16px;"">xterm.js load failed.</p>';
          return;
        }

        const container = document.getElementById('terminal-container');
        terminal = new Terminal({
          cursorBlink: true,
          cursorStyle: 'block',
          fontFamily: 'Cascadia Code, Consolas, monospace',
          fontSize: 14,
          scrollback: 10000,
          theme: {
            background: '#1e1e1e',
            foreground: '#d4d4d4',
            cursor: '#d4d4d4',
            selectionBackground: '#264f78'
          }
        });

        fitAddon = new FitAddon.FitAddon();
        terminal.loadAddon(fitAddon);
        terminal.open(container);
        fitAddon.fit();
        terminal.focus();

        terminal.onData(function(data) {
          postMessageToHost({ type: 'input', data: data });
        });

        terminal.onResize(function(size) {
          postMessageToHost({ type: 'resized', cols: size.cols, rows: size.rows });
        });

        window.addEventListener('resize', function() {
          if (fitAddon) {
            fitAddon.fit();
          }
        });

        setupMessageListener();
        postMessageToHost({ type: 'ready' });
        postMessageToHost({ type: 'resized', cols: terminal.cols, rows: terminal.rows });
      }

      if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
      } else {
        init();
      }
    })();
  </script>
</body>
</html>";
        }

        private sealed class TerminalMessage
        {
            public string type { get; set; }
            public string data { get; set; }
            public int cols { get; set; }
            public int rows { get; set; }
        }

        private sealed class Utf8DecoderHelper : IDisposable
        {
            private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
            private readonly object sync = new object();

            public string Decode(byte[] buffer, int offset, int count)
            {
                lock (sync)
                {
                    int charCount = decoder.GetCharCount(buffer, offset, count);
                    char[] chars = new char[charCount];
                    int actual = decoder.GetChars(buffer, offset, count, chars, 0);
                    return new string(chars, 0, actual);
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
