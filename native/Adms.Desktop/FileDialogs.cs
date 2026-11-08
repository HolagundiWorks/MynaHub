using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Adms.Desktop;

internal static class FileDialogs
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

    public static async Task<string?> PickFile(nint window, params string[] types)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        foreach (var type in types)
            picker.FileTypeFilter.Add(type);
        if (picker.FileTypeFilter.Count == 0)
            picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, window);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<StorageFile?> PickPdfDestination(nint window, string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
            DefaultFileExtension = ".pdf",
        };
        picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
        InitializeWithWindow.Initialize(picker, window);
        return await picker.PickSaveFileAsync();
    }

    public static async Task<StorageFile?> PickCsvDestination(nint window, string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
            DefaultFileExtension = ".csv",
        };
        picker.FileTypeChoices.Add("CSV (opens in Excel)", new List<string> { ".csv" });
        InitializeWithWindow.Initialize(picker, window);
        return await picker.PickSaveFileAsync();
    }

    /// <summary>Firm details for Tren title blocks. Not the company file and not a password.</summary>
    public static async Task<StorageFile?> PickCompanyPackDestination(nint window, string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
            DefaultFileExtension = ".json",
        };
        picker.FileTypeChoices.Add("Kivo company details", new List<string> { ".json" });
        InitializeWithWindow.Initialize(picker, window);
        return await picker.PickSaveFileAsync();
    }

    public static async Task<string?> PickFolder(nint window)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, window);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
