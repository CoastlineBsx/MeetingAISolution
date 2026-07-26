using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using MeetingAI.Host.MeetingPreparation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MeetingAI.Host.Pages;

public sealed partial class MeetingPreparationPage : Page
{
    private readonly ObservableCollection<MeetingMaterialInfo> _materials = new();
    private readonly ObservableCollection<HotwordCandidate> _hotwords = new();
    private long _preparationId;

    public MeetingPreparationPage()
    {
        InitializeComponent();
        MaterialList.ItemsSource = _materials;
        HotwordList.ItemsSource = _hotwords;
    }

    private MainWindow Host => App.MainWindow as MainWindow
        ?? throw new InvalidOperationException("主窗口尚未初始化");

    public async Task LoadPreparationAsync(long preparationId)
    {
        if (preparationId <= 0 || preparationId == _preparationId) return;
        await RunBusyAsync(async () =>
        {
            var preparation = (await Host.GetMeetingPreparationsAsync())
                .FirstOrDefault(item =>
                    item.PreparationId == preparationId)
                ?? throw new InvalidOperationException(
                    "所选会议准备档案不存在");
            _preparationId = preparation.PreparationId;
            TitleBox.Text = preparation.Title;
            await ReloadAsync();
            StatusText.Text =
                $"已打开“{preparation.Title}”，可以继续管理资料和热词。";
        });
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            _preparationId = await Host.CreateMeetingPreparationAsync(TitleBox.Text);
            _materials.Clear();
            _hotwords.Clear();
            UploadButton.IsEnabled = true;
            SaveHotwordsButton.IsEnabled = true;
            AddHotwordButton.IsEnabled = true;
            UseForMeetingButton.IsEnabled = true;
            MaterialLimitText.Text = "0 / 5";
            StatusText.Text = $"准备档案 #{_preparationId} 已建立，可以添加资料。";
        });
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        foreach (var extension in new[] { ".pptx", ".pdf", ".docx", ".txt", ".png", ".jpg", ".jpeg", ".bmp" })
            picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(Host));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        var remaining = MainWindow.MaxMeetingPreparationMaterials - _materials.Count;
        if (files.Count > remaining)
        {
            StatusText.Text = remaining <= 0
                ? "这场会议已经有 5 份资料，不能继续上传。"
                : $"这场会议最多还能添加 {remaining} 份资料，请重新选择。";
            return;
        }

        await RunBusyAsync(async () =>
        {
            foreach (var file in files)
            {
                var progress = new Progress<string>(message => StatusText.Text = $"{file.Name}：{message}");
                await Host.AddMeetingPreparationMaterialAsync(_preparationId, file.Path, progress);
            }
            await ReloadAsync();
            StatusText.Text = $"已导入 {files.Count} 个资料文件；按页知识库与热词草案已生成。";
        });
    }

    private void AddHotwordButton_Click(object sender, RoutedEventArgs e)
    {
        _hotwords.Insert(0, new HotwordCandidate
        {
            Text = "请输入术语",
            Score = 2.5,
            Enabled = true,
            SourceKind = "manual"
        });
        StatusText.Text = "已增加一条手工热词，请修改文字和权重后保存。";
    }

    private async void UseForMeetingButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            await Host.SaveMeetingPreparationHotwordsAsync(_preparationId, _hotwords);
            Host.UsePreparationForNextMeeting(_preparationId);
        });
    }

    private async void SaveHotwordsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            await Host.SaveMeetingPreparationHotwordsAsync(_preparationId, _hotwords);
            StatusText.Text = $"已保存 {_hotwords.Count} 个热词候选，其中 {CountEnabled()} 个启用。";
        });
    }

    private async Task ReloadAsync()
    {
        _materials.Clear();
        foreach (var item in await Host.GetMeetingPreparationMaterialsAsync(_preparationId)) _materials.Add(item);
        _hotwords.Clear();
        foreach (var item in await Host.GetMeetingPreparationHotwordsAsync(_preparationId)) _hotwords.Add(item);
        MaterialLimitText.Text = $"{_materials.Count} / {MainWindow.MaxMeetingPreparationMaterials}";
        UploadButton.IsEnabled = _materials.Count < MainWindow.MaxMeetingPreparationMaterials;
    }

    private int CountEnabled()
    {
        var count = 0;
        foreach (var item in _hotwords) if (item.Enabled) count++;
        return count;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        WorkProgress.Visibility = Visibility.Visible;
        CreateButton.IsEnabled = UploadButton.IsEnabled = SaveHotwordsButton.IsEnabled =
            AddHotwordButton.IsEnabled = UseForMeetingButton.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { StatusText.Text = $"处理失败：{ex.Message}"; }
        finally
        {
            WorkProgress.Visibility = Visibility.Collapsed;
            CreateButton.IsEnabled = true;
            UploadButton.IsEnabled = _preparationId > 0 &&
                                     _materials.Count < MainWindow.MaxMeetingPreparationMaterials;
            SaveHotwordsButton.IsEnabled = AddHotwordButton.IsEnabled =
                UseForMeetingButton.IsEnabled = _preparationId > 0;
        }
    }
}
