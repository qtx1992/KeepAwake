using Microsoft.Win32;                   // 用于读写 Windows 注册表
using System;                              // 基础类型与时间结构（DateTime、TimeSpan、DayOfWeek）
using System.Drawing;                      // 界面尺寸、字体、颜色与系统图标
using System.Runtime.InteropServices;        // 用于调用 Windows 底层 API（DllImport）
using System.Threading;                    // 用于单实例防多开互斥量（Mutex）
using System.Windows.Forms;                // Windows 桌面窗体与控件

internal static class Program
{
    // Windows API 标志位：持续生效 + 阻止系统休眠(1) + 阻止屏幕关闭(2)
    internal const uint Continuous = 0x80000000;
    internal const uint KeepActive = Continuous | 1 | 2;

    // 默认时间段：08:30 - 17:30，默认生效星期：周一至周日全选（bit 0~6 对应周一到周日，0x7F = 127）
    internal static readonly TimeSpan DefaultStart = new TimeSpan(8, 30, 0);
    internal static readonly TimeSpan DefaultEnd = new TimeSpan(17, 30, 0);
    internal const int AllDaysMask = 0x7F;

    // 注册表路径（当前用户 HKCU，无需管理员权限）
    private const string RegistryPath = @"Software\SimpleKeepAwake08301730";

    [DllImport("kernel32.dll")]
    internal static extern uint SetThreadExecutionState(uint flags);

    // 将 .NET 的 DayOfWeek 映射为 0~6（0=周一, 1=周二, ..., 6=周日）
    internal static int ToMondayIndex(DayOfWeek day)
    {
        return day == DayOfWeek.Sunday ? 6 : (int)day - 1;
    }

    // 判断指定星期是否已勾选
    internal static bool IsDayEnabled(int daysMask, DayOfWeek day)
    {
        int bit = 1 << ToMondayIndex(day);
        return (daysMask & bit) != 0;
    }

    // 判断指定日期时间（含星期与时分秒）是否处于防休眠生效规则内
    internal static bool IsActive(DateTime now, TimeSpan start, TimeSpan end, int daysMask)
    {
        if (start == end || (daysMask & AllDaysMask) == 0)
            return false;

        TimeSpan time = now.TimeOfDay;

        // 同一天内的时间段（例如 08:30 - 17:30）：判断当天星期是否勾选
        if (start < end)
            return IsDayEnabled(daysMask, now.DayOfWeek) && time >= start && time < end;

        // 跨午夜时间段（例如 22:00 - 次日 06:00）：
        // 若当前时间 >= start，看今天是否勾选；若当前时间 < end，看昨天（该时段起始日）是否勾选
        if (time >= start)
            return IsDayEnabled(daysMask, now.DayOfWeek);

        if (time < end)
            return IsDayEnabled(daysMask, now.AddDays(-1).DayOfWeek);

        return false;
    }

    // 启动时读取注册表中的勾选状态与时间/星期设置；默认不勾选“记住设置”
    internal static void LoadSchedule(
        out TimeSpan start,
        out TimeSpan end,
        out int daysMask,
        out bool rememberEnabled,
        out bool loadedFromSaved)
    {
        start = DefaultStart;
        end = DefaultEnd;
        daysMask = AllDaysMask;
        rememberEnabled = false; // 默认不记住设置
        loadedFromSaved = false;

        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
            {
                if (key == null)
                    return;

                // 读取上次保存的“记住设置”勾选状态
                object rawRemember = key.GetValue("RememberEnabled");
                if (rawRemember != null)
                {
                    int flag;
                    if (int.TryParse(rawRemember.ToString(), out flag))
                        rememberEnabled = flag != 0;
                }

                // 若上次未勾选“记住设置”，则恢复默认的勾选状态（false）与默认时间/星期
                if (!rememberEnabled)
                    return;

                string rawStart = key.GetValue("StartTime") as string;
                string rawEnd = key.GetValue("EndTime") as string;

                TimeSpan parsedStart;
                TimeSpan parsedEnd;
                if (!string.IsNullOrEmpty(rawStart) && !string.IsNullOrEmpty(rawEnd) &&
                    TryParseHm(rawStart.Trim(), out parsedStart) &&
                    TryParseHm(rawEnd.Trim(), out parsedEnd))
                {
                    start = parsedStart;
                    end = parsedEnd;

                    object rawDays = key.GetValue("DaysMask");
                    int parsedDays;
                    if (rawDays != null && int.TryParse(rawDays.ToString(), out parsedDays))
                        daysMask = parsedDays & AllDaysMask;

                    loadedFromSaved = true;
                }
            }
        }
        catch
        {
            // 注册表读取异常时自动回退到默认时间与未勾选状态
            start = DefaultStart;
            end = DefaultEnd;
            daysMask = AllDaysMask;
            rememberEnabled = false;
            loadedFromSaved = false;
        }
    }

    // 保存“记住设置”勾选情况及时间/星期配置；若取消勾选则清理旧时间配置并记录未勾选状态
    internal static bool TrySaveSchedule(TimeSpan start, TimeSpan end, int daysMask, bool rememberEnabled)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                if (key == null)
                    return false;

                key.SetValue("RememberEnabled", rememberEnabled ? 1 : 0, RegistryValueKind.DWord);

                if (rememberEnabled)
                {
                    key.SetValue("StartTime", string.Format("{0:hh\\:mm}", start), RegistryValueKind.String);
                    key.SetValue("EndTime", string.Format("{0:hh\\:mm}", end), RegistryValueKind.String);
                    key.SetValue("DaysMask", daysMask & AllDaysMask, RegistryValueKind.DWord);
                }
                else
                {
                    key.DeleteValue("StartTime", false);
                    key.DeleteValue("EndTime", false);
                    key.DeleteValue("DaysMask", false);
                }
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseHm(string text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        string[] parts = text.Split(':');
        if (parts.Length != 2)
            return false;

        int hour;
        int minute;
        if (!int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute))
            return false;

        if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
            return false;

        result = new TimeSpan(hour, minute, 0);
        return true;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // 命令行自检（--self-test）
        if (args.Length == 1 && args[0] == "--self-test")
        {
            DateTime thursday = new DateTime(2026, 11, 19, 0, 0, 0); // 2026-11-19 为周四
            int onlyThursday = 1 << ToMondayIndex(DayOfWeek.Thursday);

            if (IsActive(thursday.Add(new TimeSpan(8, 29, 59)), DefaultStart, DefaultEnd, AllDaysMask) ||
                !IsActive(thursday.Add(new TimeSpan(8, 30, 0)), DefaultStart, DefaultEnd, AllDaysMask) ||
                !IsActive(thursday.Add(new TimeSpan(12, 0, 0)), DefaultStart, DefaultEnd, onlyThursday) ||
                IsActive(thursday.Add(new TimeSpan(12, 0, 0)), DefaultStart, DefaultEnd, AllDaysMask & ~onlyThursday) ||
                IsActive(thursday.Add(new TimeSpan(17, 30, 0)), DefaultStart, DefaultEnd, AllDaysMask))
                return 1;

            try
            {
                return SetThreadExecutionState(KeepActive) == 0 ? 2 : 0;
            }
            finally
            {
                SetThreadExecutionState(Continuous);
            }
        }

        // 命令行界面自检（--ui-test）
        if (args.Length == 1 && args[0] == "--ui-test")
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool shown = false;
            using (AwakeForm form = new AwakeForm())
            {
                form.Shown += delegate
                {
                    // 7 个星期复选框 + startPicker + endPicker + applyBtn + rememberCheck + status + exit = 13 个控件
                    shown = form.Controls.Count == 13;
                    form.BeginInvoke(new Action(form.Close));
                };
                Application.Run(form);
            }
            return shown ? 0 : 3;
        }

        // 单实例防多开互斥锁
        bool created;
        using (Mutex instance = new Mutex(true, @"Local\SimpleKeepAwake08301730", out created))
        {
            if (!created)
            {
                // “防休眠已在运行，请查看任务栏或托盘图标。” / “防休眠”
                MessageBox.Show("\u9632\u4f11\u7720\u5df2\u5728\u8fd0\u884c\uff0c\u8bf7\u67e5\u770b\u4efb\u52a1\u680f\u6216\u6258\u76d8\u56fe\u6807\u3002",
                    "\u9632\u4f11\u7720");
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new AwakeForm());
            }
            finally
            {
                SetThreadExecutionState(Continuous);
                instance.ReleaseMutex();
            }
        }
        return 0;
    }
}

internal sealed class AwakeForm : Form
{
    private readonly CheckBox[] dayChecks = new CheckBox[7];            // 周一至周日 7 个勾选框
    private readonly DateTimePicker startPicker = new DateTimePicker();     // 开始时间选择器
    private readonly DateTimePicker endPicker = new DateTimePicker();       // 结束时间选择器
    private readonly Button applyBtn = new Button();                        // “确定”按钮
    private readonly CheckBox rememberCheck = new CheckBox();               // “记住设置（写入注册表）”开关
    private readonly Label status = new Label();                            // 状态标签（含时间来源说明）
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly ContextMenuStrip menu = new ContextMenuStrip();

    private TimeSpan startTime;     // 当前生效的开始时间
    private TimeSpan endTime;       // 当前生效的结束时间
    private int daysMask;           // 当前生效的星期位掩码（bit 0=周一 ... bit 6=周日）
    private string configSourceText;// 当前时间配置来源说明（默认时间 / 已保存设置 / 临时生效）
    private bool? applied;          // 上次成功应用的系统休眠状态

    internal AwakeForm()
    {
        bool rememberEnabled;
        bool loadedFromSaved;
        Program.LoadSchedule(out startTime, out endTime, out daysMask, out rememberEnabled, out loadedFromSaved);

        // 初始化当前配置来源说明：“已保存设置” 或 “默认时间”
        configSourceText = loadedFromSaved
            ? "\u5df2\u4fdd\u5b58\u8bbe\u7f6e"
            : "\u9ed8\u8ba4\u65f6\u95f4";

        // 窗体基本设置（“防休眠”）：适当加宽与增高，避免各行控件拥挤
        Text = "\u9632\u4f11\u7720";
        ClientSize = new Size(430, 232);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;

        // 1. 第一行：周一到周日 7 个复选框（左右留边，列间距拉宽至 58px）
        string[] dayLabels = new string[]
        {
            "\u5468\u4e00", // 周一
            "\u5468\u4e8c", // 周二
            "\u5468\u4e09", // 周三
            "\u5468\u56db", // 周四
            "\u5468\u4e94", // 周五
            "\u5468\u516d", // 周六
            "\u5468\u65e5"  // 周日
        };

        for (int i = 0; i < 7; i++)
        {
            CheckBox cb = new CheckBox();
            cb.Text = dayLabels[i];
            cb.Checked = (daysMask & (1 << i)) != 0;
            cb.AutoSize = true;
            cb.Location = new Point(18 + i * 58, 18);
            dayChecks[i] = cb;
            Controls.Add(cb);
        }

        // 2. 第二行：开始时间、结束时间与“确定”按钮
        ConfigureTimePicker(startPicker, startTime, 18, 56);
        ConfigureTimePicker(endPicker, endTime, 158, 56);

        applyBtn.Text = "\u786e\u5b9a"; // “确定”
        applyBtn.SetBounds(316, 55, 94, 30);
        applyBtn.Click += delegate { ApplySchedule(); };

        // 3. 第三行：“记住设置（写入注册表）”，默认不勾选，重启时按上次保存状态恢复
        rememberCheck.Text = "\u8bb0\u4f4f\u8bbe\u7f6e\uff08\u5199\u5165\u6ce8\u518c\u8868\uff09";
        rememberCheck.Checked = rememberEnabled;
        rememberCheck.AutoSize = true;
        rememberCheck.Location = new Point(18, 98);

        // 4. 第四行：运行状态 + 时间来源说明（例如：防休眠中（已保存设置））
        status.SetBounds(16, 132, 398, 30);
        status.TextAlign = ContentAlignment.MiddleCenter;

        // 5. 第五行：“退出”按钮
        Button exit = new Button();
        exit.Text = "\u9000\u51fa"; // “退出”
        exit.SetBounds(165, 176, 100, 34);
        exit.Click += delegate { Close(); };

        Controls.AddRange(new Control[] { startPicker, endPicker, applyBtn, rememberCheck, status, exit });

        // 配置托盘右键菜单：“显示”与“退出”
        menu.Items.Add("\u663e\u793a", null, delegate { RestoreWindow(); });
        menu.Items.Add("\u9000\u51fa", null, delegate { Close(); });

        tray.Icon = Icon;
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { RestoreWindow(); };
        tray.Visible = true;

        Resize += delegate
        {
            if (WindowState == FormWindowState.Minimized)
                Hide();
        };

        // 每 15 秒检查一次状态
        timer.Interval = 15000;
        timer.Tick += delegate { UpdateState(); };
        UpdateState();
        timer.Start();
    }

    private static void ConfigureTimePicker(DateTimePicker picker, TimeSpan initial, int x, int y)
    {
        DateTime today = DateTime.Today;
        picker.Format = DateTimePickerFormat.Custom;
        picker.CustomFormat = "HH:mm";
        picker.ShowUpDown = true;
        picker.Value = today.Add(initial);
        picker.SetBounds(x, y, 124, 30);
    }

    // 点击“确定”后的处理逻辑
    private void ApplySchedule()
    {
        startTime = new TimeSpan(startPicker.Value.Hour, startPicker.Value.Minute, 0);
        endTime = new TimeSpan(endPicker.Value.Hour, endPicker.Value.Minute, 0);

        int newMask = 0;
        for (int i = 0; i < 7; i++)
        {
            if (dayChecks[i].Checked)
                newMask |= (1 << i);
        }
        daysMask = newMask;

        bool remember = rememberCheck.Checked;
        bool saved = Program.TrySaveSchedule(startTime, endTime, daysMask, remember);

        if (remember && saved)
        {
            // “已保存设置”
            configSourceText = "\u5df2\u4fdd\u5b58\u8bbe\u7f6e";
        }
        else if (remember && !saved)
        {
            // “临时生效（写入失败）”
            configSourceText = "\u4e34\u65f6\u751f\u6548\uff08\u5199\u5165\u5931\u8d25\uff09";
        }
        else
        {
            // 未勾选“记住设置”，仅本次运行有效（若恢复为默认时间则标注默认时间，否则标注临时生效）
            bool isDefault = startTime == Program.DefaultStart &&
                             endTime == Program.DefaultEnd &&
                             daysMask == Program.AllDaysMask;
            configSourceText = isDefault
                ? "\u9ed8\u8ba4\u65f6\u95f4"
                : "\u4e34\u65f6\u751f\u6548";
        }

        // 重置状态缓存并立即刷新一次
        applied = null;
        UpdateState();

        if (!remember)
        {
            MessageBox.Show("\u65f6\u95f4\u5df2\u751f\u6548\uff08\u672a\u52fe\u9009\u8bb0\u4f4f\u8bbe\u7f6e\uff0c\u91cd\u542f\u540e\u6062\u590d\u9ed8\u8ba4\uff09\u3002",
                "\u9632\u4f11\u7720");
        }
        else
        {
            MessageBox.Show(
                saved
                    ? "\u65f6\u95f4\u8bbe\u7f6e\u5df2\u4fdd\u5b58\u5e76\u751f\u6548\u3002"
                    : "\u65f6\u95f4\u5df2\u5728\u5f53\u524c\u8fd0\u884c\u751f\u6548\uff08\u6ce8\u518c\u8868\u5199\u5165\u5931\u8d25\uff09\u3002",
                "\u9632\u4f11\u7720");
        }
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void UpdateState()
    {
        bool active = Program.IsActive(DateTime.Now, startTime, endTime, daysMask);

        // 即使 active 未变，也刷新一次状态文本，确保点击“确定”后配置来源标签立即更新
        if (applied.HasValue && applied.Value == active)
        {
            RefreshStatusLabels(active);
            return;
        }

        if (Program.SetThreadExecutionState(active ? Program.KeepActive : Program.Continuous) == 0)
        {
            applied = null;
            status.Text = string.Format("\u8bbe\u7f6e\u5931\u8d25\uff0c\u6b63\u5728\u91cd\u8bd5\uff08{0}\uff09", configSourceText);
            status.ForeColor = Color.Firebrick;
            tray.Text = "\u9632\u4f11\u7720\uff1a\u8bbe\u7f6e\u5931\u8d25";
            return;
        }

        applied = active;
        RefreshStatusLabels(active);
    }

    private void RefreshStatusLabels(bool active)
    {
        string baseState = active
            ? "\u9632\u4f11\u7720\u4e2d"                                      // “防休眠中”
            : "\u65f6\u6bb5\u5916\uff0c\u5141\u8bb8\u6b63\u5e38\u4f11\u7720"; // “时段外，允许正常休眠”

        // 拼接时间来源说明，例如：“防休眠中（已保存设置）” 或 “防休眠中（默认时间）”
        status.Text = string.Format("{0}\uff08{1}\uff09", baseState, configSourceText);
        status.ForeColor = active ? Color.ForestGreen : Color.DimGray;

        tray.Text = active
            ? string.Format("\u9632\u4f11\u7720\uff1a\u5df2\u542f\u7528\uff08\u81f3 {0:hh\\:mm}\uff09", endTime)
            : string.Format("\u9632\u4f11\u7720\uff1a\u672a\u542f\u7528\uff08{0:hh\\:mm}-{1:hh\\:mm}\uff09", startTime, endTime);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        Program.SetThreadExecutionState(Program.Continuous);
        tray.Visible = false;
        tray.Dispose();
        menu.Dispose();
        timer.Dispose();
        base.OnFormClosed(e);
    }
}