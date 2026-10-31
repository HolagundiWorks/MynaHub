using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Adms.LoginWidget;

internal static class LocalPickers
{
    public static async Task<string?> PickCompanyFile(nint window)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".osas");
        picker.FileTypeFilter.Add(".adms");
        picker.FileTypeFilter.Add(".aorms");
        InitializeWithWindow.Initialize(picker, window);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
