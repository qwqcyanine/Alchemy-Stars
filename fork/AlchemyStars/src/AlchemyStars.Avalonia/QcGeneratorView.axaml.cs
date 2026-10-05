using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AlchemyStars.Avalonia;

public partial class QcGeneratorView : UserControl
{
    public QcGeneratorView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(item => item.Path.LocalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray() ?? [];
            // Nearest ancestor StackPanel tagged with a field name is the drop target; elsewhere = auto-detect.
            var field = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<StackPanel>()
                .Select(panel => panel.Tag as string).FirstOrDefault(tag => tag is not null);
            ViewModel.DropQcPaths(paths, field);
            e.Handled = true;
        });
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;
    private async void BrowseClick(object? sender, RoutedEventArgs e) => await ViewModel.PickQcAsync((string)((Control)sender!).Tag!);
    private void RemoveAttachmentClick(object? sender, RoutedEventArgs e) => ViewModel.RemoveQcAttachment();
    private async void GenerateClick(object? sender, RoutedEventArgs e) => await ViewModel.GenerateQcAsync();
}
