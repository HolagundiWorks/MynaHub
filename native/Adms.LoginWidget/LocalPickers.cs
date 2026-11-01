using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Adms.LoginWidget;

internal static class LocalPickers
{
    public static async Task<string?> PickNewCompanyFile(nint window, string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
            DefaultFileExtension = ".osas",
        };
        picker.FileTypeChoices.Add("Company file", new List<string> { ".osas" });
        InitializeWithWindow.Initialize(picker, window);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <summary>A text file to attach to a question for the assistant.</summary>
    public static async Task<string?> PickTextFile(nint window)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in new[] { ".txt", ".md", ".csv", ".json", ".xml", ".log", ".tsv" })
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, window);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

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
