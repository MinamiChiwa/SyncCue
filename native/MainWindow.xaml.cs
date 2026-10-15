using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SyncCue.Net;
using SyncCue.Services;

namespace SyncCue;

public partial class MainWindow : Window
{
    public const string DefaultEndpoint = "ws://127.0.0.1:3131";

    private static readonly Regex UrlRx = new(
        @"https?://[^\s]+|wss?://[^\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeLineRx = new(
        @"房\s*码\s*[:：\s]*([A-Za-z0-9]{3,12})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareCodeRx = new(
        @"^[A-Za-z0-9]{3,12}$",
        RegexOptions.Compiled);

    private readonly RoomClient _client = new();
    private readonly CueSound _sound = new();
    private readonly TunnelService _tunnel = new();
    private readonly LocalServerHost _localServer = new();
    private readonly DispatcherTimer _uiTimer;
    private bool _expanded = true;
    private double _compactHeight = 360;
    private double _expandedHeight = 720;
    private double _expandedWidth = 480;
    private bool _tunnelBusy;
    private bool _tunnelAutoStopping;
    private bool _sliderUiReady;
    private bool _closingRoom;
    private string? _lastRoomCode;
    private bool _didAutoCollapse;
    private DateTime _statusHoldUntil = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        ToggleExpandButton.Content = "收起设置";
        ExpandedPanel.Visibility = Visibility.Visible;
        EndpointBox.Text = DefaultEndpoint;
        _sound.VolumePercent = (int)Math.Clamp(VolumeSlider.Value, 0, 100);
        _sliderUiReady = true;

        _client.Changed += () => Dispatcher.Invoke(RefreshUi);
        _client.Message += (text, err) => Dispatcher.Invoke(() =>
        {
            if (_closingRoom && err) return; // suppress noisy errors while we close on purpose
            SetStatus(text, err);
        });
        _tunnel.Changed += state => Dispatcher.Invoke(() =>
        {
            if (_closingRoom && state.Status == TunnelStatus.Error &&
                state.Message.Contains("取消", StringComparison.Ordinal))
            {
                ApplyTunnelState(new TunnelState
                {
                    Status = TunnelStatus.Off,
                    Message = "隧道已关闭。"
                });
                return;
            }
            ApplyTunnelState(state);
            if (state.Status == TunnelStatus.Ready && !string.IsNullOrWhiteSpace(state.Url) ||
                (state.Status == TunnelStatus.Starting && !string.IsNullOrWhiteSpace(state.Url)))
            {
                if (IsHost())
                {
                    if (!_expanded)
                    {
                        _expanded = true;
                        ApplyExpandState();
                    }
                    InvitationBox.BringIntoView();
                    SetStatus("隧道已开通，可以点「复制外网邀请」。", false);
                }
            }
        });

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += (_, _) => TickCountdown();
        _uiTimer.Start();

        Closed += async (_, _) =>
        {
            _uiTimer.Stop();
            _closingRoom = true;
            await _tunnel.StopAsync();
            await _client.DisposeAsync();
            await _localServer.DisposeAsync();
        };

        EndpointBox.TextChanged += (_, _) => UpdateEndpointHint();
        RefreshUi();
        ApplyTunnelState(_tunnel.State);
        SyncLeadVolumeLabels();
        UpdateEndpointHint();
        _ = EnsureLocalServerQuietAsync();
    }

    private async Task EnsureLocalServerQuietAsync()
    {
        try
        {
            await _localServer.EnsureListeningAsync();
            // Don't fight the top bar: only whisper if user is still on the home screen
            if (!_client.Connected && _client.Room is null &&
                !string.IsNullOrWhiteSpace(_localServer.LastMessage) &&
                _localServer.LastMessage!.Contains("监听", StringComparison.Ordinal))
            {
                Dispatcher.Invoke(() =>
                {
                    if (string.IsNullOrWhiteSpace(StatusMessage.Text))
                        SetStatus("本机房间服务已就绪，可以开房或加入。", false);
                });
            }
        }
        catch { /* ignore */ }
    }

    private void ToggleExpand_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        ApplyExpandState();
    }

    private void ApplyExpandState()
    {
        if (_expanded)
        {
            ExpandedPanel.Visibility = Visibility.Visible;
            ToggleExpandButton.Content = "收起设置";
            Width = Math.Max(Width, _expandedWidth);
            Height = Math.Max(_compactHeight + 80, _expandedHeight);
        }
        else
        {
            ExpandedPanel.Visibility = Visibility.Collapsed;
            ToggleExpandButton.Content = "展开设置";
            _expandedWidth = Width;
            _expandedHeight = Height;
            Height = _compactHeight;
        }
    }

    private void TopmostToggle_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostToggle.IsChecked == true;
    }

    private void RestoreDefaultEndpoint_Click(object sender, RoutedEventArgs e)
    {
        EndpointBox.Text = DefaultEndpoint;
        SetStatus("已恢复默认链接：" + DefaultEndpoint, false);
        UpdateEndpointHint();
    }

    private void ParseInvite_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseInvite(InvitePasteBox.Text, out var url, out var code, out var warn))
        {
            SetStatus(warn ?? "没法从这段文字里认出链接和房码。", true);
            return;
        }
        EndpointBox.Text = url!;
        CodeBox.Text = code!;
        UpdateEndpointHint();
        SetStatus("已填入链接和房码，可点「连接并加入」。", false);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(EndpointBox.Text))
                EndpointBox.Text = DefaultEndpoint;
            SetStatus("连接中…", false);
            if (IsLocalEndpoint(EndpointBox.Text))
                await _localServer.EnsureListeningAsync();
            await _client.ConnectAsync(EndpointBox.Text);
            SetStatus("已连上服务，可以加入房间。", false);
        }
        catch (Exception ex)
        {
            SetStatus("连接失败：" + ex.Message, true);
        }
        RefreshUi();
    }

    private async void ConnectAndJoin_Click(object sender, RoutedEventArgs e)
    {
        // Prefer paste box if fields empty
        if ((string.IsNullOrWhiteSpace(EndpointBox.Text) || string.IsNullOrWhiteSpace(CodeBox.Text)) &&
            !string.IsNullOrWhiteSpace(InvitePasteBox.Text))
        {
            if (TryParseInvite(InvitePasteBox.Text, out var url, out var code, out var warn))
            {
                EndpointBox.Text = url!;
                CodeBox.Text = code!;
            }
            else if (string.IsNullOrWhiteSpace(EndpointBox.Text) || string.IsNullOrWhiteSpace(CodeBox.Text))
            {
                SetStatus(warn ?? "请粘贴完整邀请，或分别填写链接和房码。", true);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(EndpointBox.Text))
        {
            SetStatus("请填写服务链接，或点「恢复默认」。", true);
            return;
        }
        if (string.IsNullOrWhiteSpace(CodeBox.Text))
        {
            SetStatus("请填写房间码（只要码，不要填端口）。", true);
            return;
        }
        if (LooksLikePort(CodeBox.Text))
        {
            SetStatus("房码不像对：你填的像端口号。请只填房间码（邀请第二行）。", true);
            return;
        }

        UpdateEndpointHint();
        if (EndpointHint.Text.Length > 0 && IsPrivateLan(EndpointBox.Text))
        {
            // soft warn already shown; still allow LAN join
        }

        try
        {
            SetStatus("正在连接并加入…", false);
            if (IsLocalEndpoint(EndpointBox.Text))
                await _localServer.EnsureListeningAsync();
            if (!_client.Connected ||
                !string.Equals(_client.Endpoint, EndpointBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await _client.ConnectAsync(EndpointBox.Text);
            }
            await _client.JoinRoomAsync(NicknameBox.Text.Trim(), CodeBox.Text.Trim());
        }
        catch (Exception ex)
        {
            var tip = ex.Message;
            if (tip.Contains("不存在", StringComparison.Ordinal) || tip.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase))
                tip += " 若你是外网朋友，请让房主重开隧道后再发你一次邀请。";
            SetStatus("加入失败：" + tip, true);
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DurationBox.Text.Trim(), out var sec) || sec < 1 || sec > 600)
        {
            SetStatus("时长应为 1–600 秒。", true);
            return;
        }
        try
        {
            if (!_client.Connected)
            {
                EndpointBox.Text = string.IsNullOrWhiteSpace(EndpointBox.Text) ? DefaultEndpoint : EndpointBox.Text;
                await _localServer.EnsureListeningAsync();
                await _client.ConnectAsync(IsLocalEndpoint(EndpointBox.Text) ? EndpointBox.Text : DefaultEndpoint);
            }
            await _client.CreateRoomAsync(NicknameBox.Text.Trim(), sec * 1000);
            SetStatus("已开房。请复制邀请发给对方。", false);
        }
        catch (Exception ex)
        {
            SetStatus("开房失败：" + ex.Message, true);
        }
    }

    private async void Join_Click(object sender, RoutedEventArgs e) => await ConnectAndJoinFlowLegacyAsync();

    private async Task ConnectAndJoinFlowLegacyAsync()
    {
        // kept for safety if old bindings linger — route to ConnectAndJoin
        ConnectAndJoin_Click(this, new RoutedEventArgs());
        await Task.CompletedTask;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_client.Room is null)
        {
            SetStatus("请先开房或加入，再开始倒计时。", true);
            return;
        }
        try
        {
            await _client.StartAsync();
            SetStatus("新一轮倒计时已开始；本轮会先清掉上一轮就绪。", false);
        }
        catch (Exception ex)
        {
            SetStatus("没法开始：" + ex.Message, true);
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e) => await _client.CancelAsync();
    private async void Leave_Click(object sender, RoutedEventArgs e) => await _client.LeaveAsync();

    private async void CloseRoom_Click(object sender, RoutedEventArgs e)
    {
        _closingRoom = true;
        SetStatus("正在关房，并通知所有人离开…", false);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        try
        {
            // Must notify members BEFORE killing the tunnel, or remote clients never get room-closed.
            await _client.CloseRoomAsync();
            _client.ClearSession();
            await Task.Delay(250);
            ApplyTunnelState(new TunnelState { Status = TunnelStatus.Stopping, Message = "正在关闭隧道…" });
            await _tunnel.StopAsync();
            ApplyTunnelState(new TunnelState { Status = TunnelStatus.Off, Message = "隧道已关闭。" });
            SetStatus("房间已关闭，成员应已离开。", false);
        }
        catch (Exception ex)
        {
            _client.ClearSession();
            try { await _tunnel.StopAsync(); } catch { /* ignore */ }
            ApplyTunnelState(new TunnelState { Status = TunnelStatus.Off, Message = "隧道已关闭。" });
            SetStatus("关房完成（" + ex.Message + "）。", false);
        }
        finally
        {
            _closingRoom = false;
            _didAutoCollapse = false;
            RefreshUi();
        }
    }

    private async void TunnelStart_Click(object sender, RoutedEventArgs e)
    {
        if (_tunnelBusy) return;
        _tunnelBusy = true;
        TunnelStartButton.IsEnabled = false;
        ApplyTunnelState(new TunnelState { Status = TunnelStatus.Starting, Message = "正在开隧道…" });
        SetStatus("正在开隧道…", false);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            if (!_client.Connected)
            {
                await _localServer.EnsureListeningAsync();
                await _client.ConnectAsync(DefaultEndpoint);
            }

            var room = _client.Room;
            var isHost = IsHost();
            if (!isHost)
            {
                ApplyTunnelState(new TunnelState
                {
                    Status = TunnelStatus.Error,
                    Message = "请先开房成为房主，再开公网隧道。"
                });
                SetStatus("请先开房成为房主，再开公网隧道。", true);
                return;
            }

            var state = await _tunnel.StartAsync(3131);
            if (_closingRoom)
            {
                ApplyTunnelState(new TunnelState { Status = TunnelStatus.Off, Message = "隧道已关闭。" });
                return;
            }
            ApplyTunnelState(state);
            if (state.Status == TunnelStatus.Ready && !string.IsNullOrWhiteSpace(state.Url))
            {
                // Force success copy — never leave "进行中"
                ApplyTunnelState(new TunnelState
                {
                    Status = TunnelStatus.Ready,
                    Url = state.Url,
                    Pid = state.Pid,
                    Message = "已开通。请点「复制外网邀请」发给外网朋友。"
                });
                SyncInvitationFromTunnel();
                ExpandInvitation();
                SetStatus("隧道已开通，可以点「复制外网邀请」。", false);
            }
            else if (state.Status == TunnelStatus.Error)
            {
                SetStatus(state.Message + " 若给外网朋友用，请修好后重开隧道再发一次。", true);
            }
        }
        catch (OperationCanceledException)
        {
            ApplyTunnelState(new TunnelState { Status = TunnelStatus.Off, Message = "隧道已关闭。" });
            if (!_closingRoom) SetStatus("隧道已取消。", false);
        }
        catch (Exception ex)
        {
            if (_closingRoom || ex.Message.Contains("取消", StringComparison.Ordinal))
            {
                ApplyTunnelState(new TunnelState { Status = TunnelStatus.Off, Message = "隧道已关闭。" });
                if (!_closingRoom) SetStatus("隧道已关闭。", false);
            }
            else
            {
                ApplyTunnelState(new TunnelState
                {
                    Status = TunnelStatus.Error,
                    Message = "开隧道失败：" + ex.Message
                });
                SetStatus("开隧道失败：" + ex.Message, true);
            }
        }
        finally
        {
            _tunnelBusy = false;
            TunnelStartButton.IsEnabled = true;
        }
    }

    private async void TunnelStop_Click(object sender, RoutedEventArgs e)
    {
        ApplyTunnelState(new TunnelState { Status = TunnelStatus.Stopping, Message = "正在关闭隧道…" });
        SetStatus("正在关闭隧道…", false);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await _tunnel.StopAsync();
        ApplyTunnelState(new TunnelState { Status = TunnelStatus.Off, Message = "隧道已关闭。" });
        SetStatus("隧道已关闭。", false);
    }

    private async void CopyLanInvite_Click(object sender, RoutedEventArgs e)
    {
        var code = _client.Room?.Code;
        var addr = LanAddress.ResolveShareAddress(
            IsLocalEndpoint(_client.Endpoint) ? _client.Endpoint : DefaultEndpoint);
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(code))
        {
            SetStatus("还没有可复制的局域网邀请。", true);
            return;
        }
        await CopyInviteBlockAsync(addr!, code!, "已复制局域网邀请（给同一 Wi‑Fi 的人）。");
    }

    private async void CopyPublicInvite_Click(object sender, RoutedEventArgs e)
    {
        var code = _client.Room?.Code;
        var url = _tunnel.State.Url;
        var publicOk = !string.IsNullOrWhiteSpace(url) &&
                      (_tunnel.State.Status is TunnelStatus.Ready or TunnelStatus.Starting);
        if (!publicOk)
        {
            SetStatus("外网邀请还没有，请先开隧道，等已开通后再复制。", true);
            return;
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            SetStatus("还没有房码。", true);
            return;
        }
        await CopyInviteBlockAsync(url!, code!, "已复制外网邀请（给外网朋友整段粘贴）。");
    }

    private async Task CopyInviteBlockAsync(string url, string code, string okMessage)
    {
        var text = url + "\n房码 " + code;
        var lastErr = 0;

        // Short fail-fast retries; await Delay so the UI stays responsive (no Thread.Sleep).
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (NativeClipboard.TrySetText(text, out lastErr))
            {
                SetStatus(okMessage, false);
                return;
            }

            // Write may have landed earlier in this loop even if a later open failed.
            if (ClipboardHasInvite(url, code))
            {
                SetStatus(okMessage, false);
                return;
            }

            await Task.Delay(40);
        }

        if (ClipboardHasInvite(url, code))
        {
            SetStatus(okMessage, false);
            return;
        }

        // Friendly line — don't dump HRESULT; content often already copied under contention.
        SetStatus("剪贴板正忙，请再点一次复制。", true);
    }

    private static bool ClipboardHasInvite(string url, string code)
    {
        try
        {
            if (!Clipboard.ContainsText()) return false;
            var clip = Clipboard.GetText() ?? "";
            return clip.Contains(url, StringComparison.OrdinalIgnoreCase) &&
                   (string.IsNullOrWhiteSpace(code) || clip.Contains(code, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private void OpenSecondWindow_Click(object sender, RoutedEventArgs e)
    {
        new MainWindow { Title = "SyncCue · 第二窗", Topmost = Topmost }.Show();
    }

    private void LeadSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_sliderUiReady) return;
        SyncLeadVolumeLabels();
    }

    private void VolumeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_sliderUiReady) return;
        _sound.VolumePercent = (int)Math.Clamp(VolumeSlider.Value, 0, 100);
        SyncLeadVolumeLabels();
    }

    private void SyncLeadVolumeLabels()
    {
        var lead = LeadMs;
        LeadValueText.Text = $"{(lead >= 0 ? "+" : "")}{lead} ms";
        VolumeValueText.Text = $"{(int)Math.Clamp(VolumeSlider.Value, 0, 100)}%";
    }

    private int LeadMs => (int)Math.Clamp(Math.Round(LeadSlider.Value), -2000, 2000);

    private void TickCountdown()
    {
        var room = _client.Room;
        var synced = _client.Clock.Offset is not null && _client.Clock.Rtt is not null;
        double? remaining = null;
        if (synced && room?.Round is not null)
            remaining = _client.Clock.Remaining(room.Round.TargetServerMs, LeadMs);

        _sound.Update(room?.Round?.Id, remaining);

        if (room is null)
        {
            CountdownText.Text = "— —";
            CueCaption.Text = "① 先选开房或加入";
            SyncStatus.Text = _client.Connected ? "已连上服务，还没进房" : "等待进房";
            return;
        }

        if (!synced)
        {
            CountdownText.Text = "— —";
            CueCaption.Text = "正在对齐时间…";
            SyncStatus.Text = "正在对时…";
        }
        else if (room.Round is null || remaining is null)
        {
            CountdownText.Text = "待命";
            CueCaption.Text = IsHost() ? "可以点开始倒计时" : "等待房主开始";
            SyncStatus.Text = $"已对时 · RTT {_client.Clock.Rtt:0.0} ms";
        }
        else
        {
            var rem = remaining.Value;
            if (rem > 0)
            {
                CountdownText.Text = Math.Ceiling(rem / 1000.0).ToString("0");
                CueCaption.Text = rem <= 5500 ? "末秒提示" : "倒计时中";
                SyncStatus.Text = $"已对时 · RTT {_client.Clock.Rtt:0.0} ms";
            }
            else if (rem > -800)
            {
                CountdownText.Text = "0";
                CueCaption.Text = "开拍";
                SyncStatus.Text = $"已对时 · RTT {_client.Clock.Rtt:0.0} ms";
            }
            else
            {
                // Past hit — treat as idle so host can start round 2/3 cleanly.
                CountdownText.Text = "待命";
                CueCaption.Text = IsHost() ? "可以再开下一轮" : "等待房主再开";
                SyncStatus.Text = $"已对时 · RTT {_client.Clock.Rtt:0.0} ms";
                _sound.Update(null, null);
            }
        }
    }

    private void RefreshUi()
    {
        var room = _client.Room;
        var inRoom = room is not null;

        // Same room can re-start after the current hit; disable only while counting down.
        var roundActive = false;
        if (inRoom && room?.Round is not null &&
            _client.Clock.Offset is not null && _client.Clock.Rtt is not null)
        {
            var rem = _client.Clock.Remaining(room.Round.TargetServerMs, LeadMs);
            roundActive = rem is > 0;
        }
        var canStart = inRoom && !roundActive && IsHost();
        // cohost may also start per server; mirror hostOrCohost
        if (inRoom && !roundActive)
        {
            var self = room!.Members.FirstOrDefault(m => m.Id == _client.Session?.MemberId);
            canStart = self?.Role is "host" or "cohost";
        }
        CompactStartButton.IsEnabled = canStart;
        CompactStartButton.Opacity = canStart ? 1.0 : 0.45;
        ExpandedStartButton.IsEnabled = canStart;
        ExpandedStartButton.Opacity = canStart ? 1.0 : 0.45;

        if (!inRoom)
            ConnectionBadge.Text = _client.Connected ? "已连服务 · 未进房" : "未进房";
        else
            ConnectionBadge.Text = "已进房";
        ConnectionBadge.Foreground = inRoom
            ? Brushes.LightGreen
            : (_client.Connected
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7EB6FF")!)
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0A020")!));

        RoomLabel.Text = room is null ? "还没进房" : $"房间 {room.Code}";
        if (room is not null && CodeBox.Text != room.Code && !CodeBox.IsKeyboardFocusWithin)
            CodeBox.Text = room.Code;
        if (room is not null && !DurationBox.IsKeyboardFocusWithin)
            DurationBox.Text = (room.DurationMs / 1000).ToString();

        MembersCard.Visibility = inRoom ? Visibility.Visible : Visibility.Collapsed;
        MembersList.Items.Clear();
        if (room is null)
        {
            MembersSummaryText.Text = "";
            ApplyReadyButtons(showReady: false, selfReady: false);
        }
        else
        {
            var connected = room.Members.Where(m => m.Connected).ToList();
            var readyCount = connected.Count(m => m.Ready);
            MembersSummaryText.Text = $"已就绪 {readyCount}/{Math.Max(connected.Count, 1)} · 共 {room.Members.Count} 人";
            foreach (var m in room.Members)
            {
                var me = m.Id == _client.Session?.MemberId ? "（我）" : "";
                var ready = !m.Connected ? "● 掉线" : m.Ready ? "● 已就绪" : "○ 未就绪";
                var role = m.Role switch { "host" => "房主", "cohost" => "协管", _ => "成员" };
                MembersList.Items.Add($"{ready}   {m.Nickname}{me}   ·   {role}");
            }

            var self = room.Members.FirstOrDefault(m => m.Id == _client.Session?.MemberId);
            var selfReady = self?.Ready == true;
            // Host does not need ready; only participants see the ready buttons beside Start.
            var showReady = self is not null && self.Role is not "host";
            ApplyReadyButtons(showReady, selfReady);
        }

        var isHost = IsHost();
        SyncInvitationFromTunnel();
        if (isHost && room is not null)
        {
            if (_lastRoomCode != room.Code)
            {
                _lastRoomCode = room.Code;
                ExpandInvitation();
                SetStatus("已开房。请复制邀请发给对方（局域网或先开隧道再复制外网）。", false);
            }
        }
        else
        {
            _lastRoomCode = null;
        }

        if (room is null && _client.Session is null &&
            _tunnel.State.Status is TunnelStatus.Ready or TunnelStatus.Starting)
        {
            _ = AutoStopTunnelAsync();
        }

        // Joiners: collapse once so countdown + start dominate.
        // Hosts: stay expanded so invite / tunnel copy stay visible.
        if (inRoom && !_didAutoCollapse && _expanded && !isHost)
        {
            _didAutoCollapse = true;
            _expanded = false;
            ApplyExpandState();
        }
        if (!inRoom)
            _didAutoCollapse = false;

        SyncLeadVolumeLabels();
        TickCountdown();
    }

    private void ExpandInvitation()
    {
        if (!_expanded)
        {
            _expanded = true;
            ApplyExpandState();
        }
        InvitationBox.BringIntoView();
    }

    private async Task AutoStopTunnelAsync()
    {
        if (_tunnelAutoStopping || _closingRoom) return;
        _tunnelAutoStopping = true;
        try
        {
            await _tunnel.StopAsync();
            Dispatcher.Invoke(() =>
            {
                ApplyTunnelState(new TunnelState { Status = TunnelStatus.Off, Message = "隧道已关闭。" });
            });
        }
        catch { /* ignore */ }
        finally { _tunnelAutoStopping = false; }
    }


    private void ApplyReadyButtons(bool showReady, bool selfReady)
    {
        var vis = showReady ? Visibility.Visible : Visibility.Collapsed;
        CompactReadyButton.Visibility = vis;
        ExpandedReadyButton.Visibility = vis;
        var label = selfReady ? "取消就绪" : "我已就绪";
        CompactReadyButton.Content = label;
        ExpandedReadyButton.Content = label;
    }

    private async void ReadyToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_client.Room is null || _client.Session is null)
        {
            SetStatus("请先进入房间再点就绪。", true);
            return;
        }
        var self = _client.Room.Members.FirstOrDefault(m => m.Id == _client.Session.MemberId);
        var next = !(self?.Ready ?? false);
        CompactReadyButton.IsEnabled = false; ExpandedReadyButton.IsEnabled = false;
        try
        {
            await _client.SetReadyAsync(next);
            SetStatus(next ? "你已就绪。" : "已取消就绪。", false);
        }
        catch (Exception ex)
        {
            SetStatus("就绪切换失败：" + ex.Message, true);
        }
        finally
        {
            CompactReadyButton.IsEnabled = true; ExpandedReadyButton.IsEnabled = true;
        }
    }

    private void SyncInvitationFromTunnel(TunnelState? display = null)
    {
        var isHost = IsHost();
        var room = _client.Room;
        InvitationBox.Visibility = isHost ? Visibility.Visible : Visibility.Collapsed;
        if (!isHost || room is null)
        {
            CopyPublicInviteButton.IsEnabled = false;
            CopyPublicInviteButton.Opacity = 0.5;
            return;
        }

        var tunnel = display ?? _tunnel.State;
        // Same readiness as tunnel badge: URL present while Starting (probing) or Ready.
        var publicReady = !string.IsNullOrWhiteSpace(tunnel.Url) &&
                          (tunnel.Status is TunnelStatus.Ready or TunnelStatus.Starting);
        var publicUrl = publicReady ? tunnel.Url : null;

        var lan = LanAddress.ResolveShareAddress(
            IsLocalEndpoint(_client.Endpoint) ? _client.Endpoint : DefaultEndpoint);

        ShareAddressText.Text = publicUrl is not null
            ? $"外网：{publicUrl}\n局域网：{lan ?? "（未检测到）"}"
            : $"局域网：{lan ?? "（未检测到）"}\n外网：还没开隧道";
        ShareCodeText.Text = $"房码 {room.Code}";

        CopyPublicInviteButton.IsEnabled = publicReady;
        CopyPublicInviteButton.Opacity = publicReady ? 1.0 : 0.5;
    }

    private void ApplyTunnelState(TunnelState state)
    {
        // Normalize lingering "starting" if we somehow have a URL
        if (state.Status == TunnelStatus.Starting && !string.IsNullOrWhiteSpace(state.Url))
        {
            state = new TunnelState
            {
                Status = TunnelStatus.Ready,
                Url = state.Url,
                Pid = state.Pid,
                Message = "已开通。请点「复制外网邀请」发给外网朋友。"
            };
        }

        TunnelBadge.Text = state.Status switch
        {
            TunnelStatus.Off => "未开",
            TunnelStatus.Starting => "开通中",
            TunnelStatus.Ready => "已开通",
            TunnelStatus.Stopping => "关闭中",
            TunnelStatus.Error => "出错",
            _ => state.Status.ToString()
        };
        TunnelBadge.Foreground = state.Status switch
        {
            TunnelStatus.Ready => Brushes.LightGreen,
            TunnelStatus.Error => Brushes.Salmon,
            TunnelStatus.Starting or TunnelStatus.Stopping =>
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0A020")!),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9AA3B2")!)
        };
        TunnelUrlText.Text = state.Url ?? "";
        TunnelMessageText.Text = state.Status == TunnelStatus.Ready &&
                                 !state.Message.Contains("已开通", StringComparison.Ordinal)
            ? "已开通。请点「复制外网邀请」发给外网朋友。"
            : state.Message;
        TunnelMessageText.Foreground = state.Status switch
        {
            TunnelStatus.Ready => Brushes.LightGreen,
            TunnelStatus.Error => Brushes.Salmon,
            TunnelStatus.Starting or TunnelStatus.Stopping =>
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0A020")!),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9AA3B2")!)
        };

        // Same beat as badge: invite area + copy button follow display state.
        SyncInvitationFromTunnel(state);
        if (state.Status == TunnelStatus.Ready && !string.IsNullOrWhiteSpace(state.Url) && IsHost())
            SetStatus("隧道已开通，可以点「复制外网邀请」。", false);
    }

    private void UpdateEndpointHint()
    {
        var ep = EndpointBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ep))
        {
            EndpointHint.Text = "";
            return;
        }
        if (IsPrivateLan(ep) && !IsLocalEndpoint(ep))
        {
            EndpointHint.Text = "这是内网地址：外网朋友通常进不来。外网请用房主的「复制外网邀请」。";
            return;
        }
        if (ep.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) &&
            ep.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase))
        {
            EndpointHint.Text = "这是外网隧道地址，适合外网朋友加入。";
            return;
        }
        EndpointHint.Text = "";
    }

    private bool IsHost()
    {
        var room = _client.Room;
        return room is not null &&
               _client.Session is not null &&
               room.Members.Any(m => m.Id == _client.Session.MemberId && m.Role == "host");
    }

    private static bool IsLocalEndpoint(string endpoint)
    {
        try
        {
            var u = new Uri(endpoint.Contains("://") ? endpoint : "ws://" + endpoint);
            return u.Host is "127.0.0.1" or "localhost" or "::1";
        }
        catch { return false; }
    }

    private static bool IsPrivateLan(string endpoint)
    {
        try
        {
            var raw = endpoint.Trim();
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                raw = "ws://" + raw[7..];
            if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                raw = "wss://" + raw[8..];
            var u = new Uri(raw.Contains("://") ? raw : "ws://" + raw);
            var h = u.Host;
            return h.StartsWith("192.168.") || h.StartsWith("10.") ||
                   Regex.IsMatch(h, @"^172\.(1[6-9]|2\d|3[0-1])\.");
        }
        catch { return false; }
    }

    private static bool LooksLikePort(string code)
    {
        var t = code.Trim();
        return t is "3131" or "80" or "443" or "8080" || (t.Length <= 5 && int.TryParse(t, out var n) && n is >= 1 and <= 65535 && t.All(char.IsDigit));
    }

    public static bool TryParseInvite(string text, out string? url, out string? code, out string? warn)
    {
        url = null;
        code = null;
        warn = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            warn = "粘贴内容是空的。";
            return false;
        }

        var m = UrlRx.Match(text);
        if (!m.Success)
        {
            warn = "没有找到链接（应以 ws:// 或 wss:// 开头）。";
            return false;
        }

        var raw = m.Value.Trim().TrimEnd('/', '。', '，', ',', ';');
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            raw = "ws://" + raw[7..];
        else if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = "wss://" + raw[8..];
        url = raw;

        var cm = CodeLineRx.Match(text);
        if (cm.Success)
        {
            code = cm.Groups[1].Value;
        }
        else
        {
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.Contains("://", StringComparison.Ordinal)) continue;
                if (t.StartsWith("房", StringComparison.Ordinal)) continue;
                if (BareCodeRx.IsMatch(t) && !LooksLikePort(t))
                {
                    code = t;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            warn = "找到了链接，但没找到房码。请确认第二行是「房码 XXXX」。";
            return false;
        }
        if (LooksLikePort(code))
        {
            warn = "房码读起来像端口号，请检查邀请第二行。";
            return false;
        }
        return true;
    }

    private void SetStatus(string text, bool error)
    {
        // Keep "已复制…" visible briefly; tunnel Ready spam must not overwrite it.
        var now = DateTime.UtcNow;
        var isCopyFeedback = text.Contains("已复制", StringComparison.Ordinal) ||
                             text.Contains("复制失败", StringComparison.Ordinal);
        if (!isCopyFeedback && !error && now < _statusHoldUntil)
            return;

        if (isCopyFeedback)
            _statusHoldUntil = now.AddSeconds(4);

        StatusMessage.Text = text;
        StatusMessage.Foreground = error
            ? Brushes.Salmon
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A8B0C0")!);
    }
}
